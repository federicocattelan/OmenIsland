using System.Diagnostics;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OmenIsland.App.Services.Media;

public sealed record NowPlayingSnapshot(
    bool IsActive,
    string TrackKey,
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    bool Shuffle,
    MediaRepeat Repeat,
    byte[]? Artwork,
    double Volume,
    bool HasVolume,
    IReadOnlyList<int>? ProcessIds = null)
{
    public IReadOnlyList<int> ProcessIds { get; init; } = ProcessIds ?? [];

    public static NowPlayingSnapshot Empty { get; } = new(
        false,
        "",
        "",
        "",
        TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        false,
        MediaRepeat.Off,
        null,
        100,
        false,
        []);
}

public sealed class NowPlayingService : IAsyncDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _changedAt = new(StringComparer.Ordinal);
    private readonly List<GlobalSystemMediaTransportControlsSession> _hooked = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private DateTime _lastPoll = DateTime.MinValue;
    private byte[]? _artwork;
    private string _artworkKey = "";
    private int[] _processIds = [];
    private int _dirty;

    public NowPlayingSnapshot Current { get; private set; } = NowPlayingSnapshot.Empty;

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        await RefreshAsync();
    }

    public void AdvanceTimeline()
    {
        if (DateTime.UtcNow - _lastPoll >= TimeSpan.FromSeconds(1))
        {
            _lastPoll = DateTime.UtcNow;
            _ = RefreshAsync();
        }

        var session = _session;
        if (session is null || !Current.IsActive)
        {
            return;
        }

        try
        {
            var clock = ReadClock(session);
            if ((int)Current.Position.TotalSeconds == (int)clock.Position.TotalSeconds &&
                Current.Duration == clock.Duration &&
                Current.IsPlaying == clock.Playing)
            {
                return;
            }

            Current = Current with
            {
                Position = clock.Position,
                Duration = clock.Duration,
                IsPlaying = clock.Playing
            };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The session can close between polls.
        }
    }

    public async Task TogglePlayAsync() =>
        await TryAsync(session => session.TryTogglePlayPauseAsync());

    public async Task NextAsync() =>
        await TryAsync(session => session.TrySkipNextAsync());

    public async Task PreviousAsync() =>
        await TryAsync(session => session.TrySkipPreviousAsync());

    public async Task SeekAsync(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        await TryAsync(session => session.TryChangePlaybackPositionAsync(position.Ticks));
        AdvanceTimeline();
    }

    public async Task ToggleShuffleAsync()
    {
        var enabled = !Current.Shuffle;
        await TryAsync(session => session.TryChangeShuffleActiveAsync(enabled));
        MarkDirty();
    }

    public async Task CycleRepeatAsync()
    {
        var next = Current.Repeat switch
        {
            MediaRepeat.Off => MediaPlaybackAutoRepeatMode.List,
            MediaRepeat.All => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None
        };
        await TryAsync(session => session.TryChangeAutoRepeatModeAsync(next));
        MarkDirty();
    }

    public void SetVolume(double percent)
    {
        if (_processIds.Length == 0)
        {
            return;
        }

        ProcessAudioVolume.TrySet(_processIds, (float)(percent / 100d));
    }

    public async ValueTask DisposeAsync()
    {
        Unhook();
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        _refresh.Dispose();
        await Task.CompletedTask;
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) =>
        MarkDirty();

    private void MarkDirty()
    {
        Interlocked.Increment(ref _dirty);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!await _refresh.WaitAsync(0))
        {
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _dirty, 0);
                await ReadSessionsAsync();
            }
            while (Volatile.Read(ref _dirty) > 0);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task ReadSessionsAsync()
    {
        if (_manager is null)
        {
            return;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = _manager.GetSessions();
        }
        catch
        {
            if (!SessionStillActive())
            {
                Publish(NowPlayingSnapshot.Empty, null, []);
            }

            return;
        }

        var allSessions = sessions.ToList();
        try
        {
            var current = _manager.GetCurrentSession();
            if (current is not null && !allSessions.Contains(current))
            {
                allSessions.Insert(0, current);
            }
        }
        catch
        {
            // The current session is optional.
        }

        var candidates = new List<(MediaCandidate Candidate, GlobalSystemMediaTransportControlsSession Session)>();
        for (var index = 0; index < allSessions.Count; index++)
        {
            var session = allSessions[index];
            var rawId = session.SourceAppUserModelId;
            var appId = string.IsNullOrWhiteSpace(rawId) ? "media" : rawId;
            var source = string.IsNullOrWhiteSpace(rawId)
                ? MediaSourceKind.Other
                : MediaApp.Classify(rawId);
            if (source == MediaSourceKind.None)
            {
                continue;
            }

            MediaActivity? activity;
            try
            {
                activity = session.GetPlaybackInfo()?.PlaybackStatus switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaActivity.Playing,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaActivity.Paused,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaActivity.Playing,
                    _ => null
                };
            }
            catch
            {
                continue;
            }

            if (activity is null)
            {
                continue;
            }

            var candidateId = appId + "#" + index;
            if (!_changedAt.ContainsKey(candidateId))
            {
                _changedAt[candidateId] = DateTimeOffset.UtcNow;
            }

            candidates.Add((
                new MediaCandidate(candidateId, source, activity.Value, _changedAt[candidateId]),
                session));
        }

        var selected = MediaSessionSelector.Select(
            candidates.Select(item => item.Candidate).ToList());
        if (selected is null)
        {
            if (!SessionStillActive())
            {
                Publish(NowPlayingSnapshot.Empty, null, []);
            }

            return;
        }

        var match = candidates.First(item => item.Candidate.Id == selected.Id).Session;
        await PublishSessionAsync(match, selected.Source);
    }

    private async Task PublishSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        MediaSourceKind source)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
        var position = TimeSpan.Zero;
        var duration = TimeSpan.Zero;
        try
        {
            properties = await session.TryGetMediaPropertiesAsync();
            playback = session.GetPlaybackInfo();
            var clock = ReadClock(session);
            position = clock.Position;
            duration = clock.Duration;
        }
        catch
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(properties?.Title) ? "Senza titolo" : properties.Title;
        var artist = properties?.Artist ?? "";
        var trackKey = $"{source}|{title}|{artist}|{duration.Ticks}";
        var artwork = _artwork;
        if (!string.Equals(_artworkKey, trackKey, StringComparison.Ordinal))
        {
            artwork = await ReadArtworkAsync(properties?.Thumbnail);
            _artwork = artwork;
            _artworkKey = trackKey;
        }

        var repeat = playback?.AutoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.List => MediaRepeat.All,
            MediaPlaybackAutoRepeatMode.Track => MediaRepeat.One,
            _ => MediaRepeat.Off
        };
        var processName = MediaApp.ProcessName(session.SourceAppUserModelId);
        var processIds = ProcessIds(processName);
        var volume = ProcessAudioVolume.TryGet(processIds);
        Publish(
            new NowPlayingSnapshot(
                true,
                trackKey,
                title,
                artist,
                position,
                duration,
                playback?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback?.IsShuffleActive == true,
                repeat,
                artwork,
                volume is null ? Current.Volume : Math.Round(volume.Value * 100d),
                volume is not null,
                processIds),
            session,
            processIds);
    }

    private void Publish(
        NowPlayingSnapshot snapshot,
        GlobalSystemMediaTransportControlsSession? session,
        int[] processIds)
    {
        _session = session;
        _processIds = processIds;
        if (!snapshot.IsActive)
        {
            _artwork = null;
            _artworkKey = "";
        }

        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (TimeSpan Position, TimeSpan Duration, bool Playing) ReadClock(
        GlobalSystemMediaTransportControlsSession session)
    {
        var timeline = session.GetTimelineProperties();
        var playing = session.GetPlaybackInfo()?.PlaybackStatus is
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;
        var start = timeline.StartTime > TimeSpan.Zero ? timeline.StartTime : TimeSpan.Zero;
        var end = timeline.EndTime > start ? timeline.EndTime : start;
        var position = timeline.Position < start ? start : timeline.Position;
        if (playing &&
            timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch &&
            timeline.LastUpdatedTime < DateTimeOffset.Now.AddMinutes(1))
        {
            var sinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (sinceUpdate > TimeSpan.Zero && sinceUpdate < TimeSpan.FromMinutes(30))
            {
                position += sinceUpdate;
            }
        }

        if (position > end && end > TimeSpan.Zero)
        {
            position = end;
        }

        return (position - start, end - start, playing);
    }

    private bool SessionStillActive()
    {
        var session = _session;
        if (session is null || !Current.IsActive)
        {
            return false;
        }

        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus is
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused or
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;
        }
        catch
        {
            return false;
        }
    }

    private void Hook(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        Unhook();
        foreach (var session in sessions)
        {
            session.PlaybackInfoChanged += OnPlaybackChanged;
            session.MediaPropertiesChanged += OnMediaChanged;
            _hooked.Add(session);
        }
    }

    private void Unhook()
    {
        foreach (var session in _hooked)
        {
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.MediaPropertiesChanged -= OnMediaChanged;
        }

        _hooked.Clear();
    }

    private void OnPlaybackChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        Touch(sender.SourceAppUserModelId);
        MarkDirty();
    }

    private void OnMediaChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        Touch(sender.SourceAppUserModelId);
        MarkDirty();
    }

    private void Touch(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        _changedAt[appId] = DateTimeOffset.UtcNow;
    }

    private async Task TryAsync(
        Func<GlobalSystemMediaTransportControlsSession, global::Windows.Foundation.IAsyncOperation<bool>> call)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await call(session);
        }
        catch
        {
            // The player can reject a control it does not support.
        }
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var size = stream.Size;
            if (size == 0 || size > 8_000_000)
            {
                return null;
            }

            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static int[] ProcessIds(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        try
        {
            return Process.GetProcessesByName(processName)
                .Select(process =>
                {
                    var id = process.Id;
                    process.Dispose();
                    return id;
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
