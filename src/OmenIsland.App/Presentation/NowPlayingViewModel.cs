using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmenIsland.App.Services.Media;

namespace OmenIsland.App.Presentation;

public sealed class NowPlayingViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NowPlayingService _service;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private bool _seeking;
    private bool _applying;
    private byte[]? _artworkBytes;
    private string _trackKey = "";
    private string _title = "";
    private string _artist = "";
    private string _elapsedText = "0:00";
    private string _durationText = "0:00";
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 100;
    private bool _isActive;
    private bool _isVisualizerOpen;
    private bool _isTerrainOpen;
    private bool _isPlaying;
    private bool _isShuffleOn;
    private bool _hasVolume;
    private MediaRepeat _repeat = MediaRepeat.Off;
    private ImageSource? _artwork;
    private IReadOnlyList<int> _processIds = [];
    private int _activeProcessId;

    public IReadOnlyList<int> ProcessIds
    {
        get => _processIds;
        private set => SetProperty(ref _processIds, value);
    }

    public int ActiveProcessId
    {
        get => _activeProcessId;
        private set => SetProperty(ref _activeProcessId, value);
    }

    public NowPlayingViewModel(NowPlayingService service)
    {
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.Changed += (_, _) =>
        {
            var snapshot = _service.Current;
            if (_dispatcher.CheckAccess())
            {
                Apply(snapshot);
            }
            else
            {
                _dispatcher.Invoke(() => Apply(snapshot));
            }
        };

        TogglePlayCommand = new AsyncRelayCommand(
            () => _service.TogglePlayAsync(),
            () => IsActive);
        PreviousCommand = new AsyncRelayCommand(
            () => _service.PreviousAsync(),
            () => IsActive);
        NextCommand = new AsyncRelayCommand(
            () => _service.NextAsync(),
            () => IsActive);
        ToggleShuffleCommand = new AsyncRelayCommand(
            () => _service.ToggleShuffleAsync(),
            () => IsActive);
        CycleRepeatCommand = new AsyncRelayCommand(
            () => _service.CycleRepeatAsync(),
            () => IsActive);
        ToggleVisualizerCommand = new RelayCommand(_ => IsVisualizerOpen = !IsVisualizerOpen);
        ToggleTerrainCommand = new RelayCommand(_ => IsTerrainOpen = !IsTerrainOpen);
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                RaiseCommands();
                OnPropertyChanged(nameof(ShowVisualizer));
                OnPropertyChanged(nameof(ShowTerrain));
            }
        }
    }

    public bool IsVisualizerOpen
    {
        get => _isVisualizerOpen;
        set
        {
            if (SetProperty(ref _isVisualizerOpen, value))
            {
                OnPropertyChanged(nameof(ShowVisualizer));
            }
        }
    }

    public bool IsTerrainOpen
    {
        get => _isTerrainOpen;
        set
        {
            if (SetProperty(ref _isTerrainOpen, value))
            {
                OnPropertyChanged(nameof(ShowTerrain));
            }
        }
    }

    public bool ShowVisualizer => IsActive && IsVisualizerOpen;

    public bool ShowTerrain => IsActive && IsTerrainOpen;

    public ICommand ToggleVisualizerCommand { get; }

    public ICommand ToggleTerrainCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        private set => SetProperty(ref _artist, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set => SetProperty(ref _positionSeconds, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetProperty(ref _durationSeconds, value);
    }

    public double Progress =>
        DurationSeconds <= 0 ? 0 : Math.Clamp(PositionSeconds / DurationSeconds, 0, 1);

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, value) || _applying)
            {
                return;
            }

            _service.SetVolume(value);
        }
    }

    public bool HasVolume
    {
        get => _hasVolume;
        private set => SetProperty(ref _hasVolume, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayGlyph));
            }
        }
    }

    public bool IsShuffleOn
    {
        get => _isShuffleOn;
        private set => SetProperty(ref _isShuffleOn, value);
    }

    public bool IsRepeatOn => _repeat != MediaRepeat.Off;

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    public string RepeatGlyph => _repeat == MediaRepeat.One ? "\uE8ED" : "\uE8EE";

    public ImageSource? Artwork
    {
        get => _artwork;
        private set => SetProperty(ref _artwork, value);
    }

    public ICommand TogglePlayCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand ToggleShuffleCommand { get; }

    public ICommand CycleRepeatCommand { get; }

    public Task StartAsync()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _service.AdvanceTimeline();
        _timer.Start();
        return _service.StartAsync();
    }

    public void BeginSeek() => _seeking = true;

    public void EndSeek(double seconds)
    {
        _seeking = false;
        PositionSeconds = seconds;
        ElapsedText = MediaApp.FormatClock(TimeSpan.FromSeconds(seconds));
        OnPropertyChanged(nameof(Progress));
        _ = _service.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    }

    public async ValueTask DisposeAsync()
    {
        _timer?.Stop();
        await _service.DisposeAsync();
    }

    private void Apply(NowPlayingSnapshot snapshot)
    {
        IsActive = snapshot.IsActive;
        var ids = snapshot.ProcessIds ?? [];
        ProcessIds = ids;
        ActiveProcessId = snapshot.IsActive ? ProcessLoopbackCapture.ResolveActiveProcessId(ids) : 0;
        Title = snapshot.Title;
        Artist = snapshot.Artist;
        IsPlaying = snapshot.IsPlaying;
        IsShuffleOn = snapshot.Shuffle;
        if (_repeat != snapshot.Repeat)
        {
            _repeat = snapshot.Repeat;
            OnPropertyChanged(nameof(RepeatGlyph));
            OnPropertyChanged(nameof(IsRepeatOn));
        }

        HasVolume = snapshot.HasVolume || snapshot.IsActive;
        var trackChanged = !string.Equals(_trackKey, snapshot.TrackKey, StringComparison.Ordinal);
        if (snapshot.HasVolume && trackChanged)
        {
            _applying = true;
            Volume = snapshot.Volume;
            _applying = false;
        }

        DurationSeconds = snapshot.Duration.TotalSeconds;
        DurationText = MediaApp.FormatClock(snapshot.Duration);
        if (!_seeking)
        {
            PositionSeconds = snapshot.Position.TotalSeconds;
            ElapsedText = MediaApp.FormatClock(snapshot.Position);
            OnPropertyChanged(nameof(Progress));
        }

        if (!string.Equals(_trackKey, snapshot.TrackKey, StringComparison.Ordinal) ||
            snapshot.Artwork != _artworkBytes)
        {
            _trackKey = snapshot.TrackKey;
            _artworkBytes = snapshot.Artwork;
            Artwork = CreateImage(snapshot.Artwork);
        }
    }

    private void RaiseCommands()
    {
        (TogglePlayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreviousCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (NextCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ToggleShuffleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CycleRepeatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
