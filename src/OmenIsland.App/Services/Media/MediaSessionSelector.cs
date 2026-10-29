using System.IO;

namespace OmenIsland.App.Services.Media;

public enum MediaSourceKind
{
    None,
    Spotify,
    YouTube,
    Other
}

public enum MediaActivity
{
    Playing,
    Paused
}

public enum MediaRepeat
{
    Off,
    All,
    One
}

public sealed record MediaCandidate(
    string Id,
    MediaSourceKind Source,
    MediaActivity Activity,
    DateTimeOffset ChangedAt);

public static class MediaSessionSelector
{
    public static MediaCandidate? Select(IReadOnlyList<MediaCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var playing = candidates
            .Where(candidate => candidate.Activity == MediaActivity.Playing)
            .ToList();
        var pool = playing.Count > 0 ? playing : candidates;
        return pool
            .OrderByDescending(candidate => candidate.ChangedAt)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .First();
    }
}

public static class MediaApp
{
    public static MediaSourceKind Classify(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return MediaSourceKind.None;
        }

        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceKind.Spotify;
        }

        return MediaSourceKind.Other;
    }

    public static bool IsBrowser(string appId) =>
        ContainsAny(appId, "chrome", "msedge", "firefox", "brave", "opera");

    public static string? ProcessName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase))
        {
            return "msedge";
        }

        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "chrome";
        }

        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase))
        {
            return "firefox";
        }

        if (appId.Contains("brave", StringComparison.OrdinalIgnoreCase))
        {
            return "brave";
        }

        if (appId.Contains("opera", StringComparison.OrdinalIgnoreCase))
        {
            return "opera";
        }

        var tail = appId.Split('\\', '/').Last();
        var name = tail.Split('!').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(name);
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static bool TitleLooksLikeYouTube(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        title.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

    public static string FormatClock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var totalSeconds = (int)time.TotalSeconds;
        var minutes = totalSeconds / 60;
        return minutes >= 100
            ? $"{minutes}:{totalSeconds % 60:00}"
            : $"{minutes:00}:{totalSeconds % 60:00}";
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment =>
            value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
