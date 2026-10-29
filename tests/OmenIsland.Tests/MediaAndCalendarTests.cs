using OmenIsland.App.Services;
using OmenIsland.App.Services.Calendar;
using OmenIsland.App.Services.Media;

namespace OmenIsland.Tests;

public sealed class MediaAndCalendarTests
{
    [Fact]
    public void PlayingSessionWinsOverPausedOne()
    {
        var now = DateTimeOffset.Parse("2026-09-29T18:00:00Z");
        var selected = MediaSessionSelector.Select(
        [
            new MediaCandidate("spotify", MediaSourceKind.Spotify, MediaActivity.Paused, now),
            new MediaCandidate("chrome", MediaSourceKind.YouTube, MediaActivity.Playing, now.AddMinutes(-5))
        ]);

        Assert.Equal("chrome", selected?.Id);
    }

    [Fact]
    public void LatestPlayingSessionWinsWhenBothAreActive()
    {
        var now = DateTimeOffset.Parse("2026-09-29T18:00:00Z");
        var selected = MediaSessionSelector.Select(
        [
            new MediaCandidate("spotify", MediaSourceKind.Spotify, MediaActivity.Playing, now.AddMinutes(-2)),
            new MediaCandidate("chrome", MediaSourceKind.YouTube, MediaActivity.Playing, now)
        ]);

        Assert.Equal("chrome", selected?.Id);
    }

    [Fact]
    public void PausedSessionStaysAvailableWhenNothingIsPlaying()
    {
        var selected = MediaSessionSelector.Select(
        [
            new MediaCandidate(
                "spotify",
                MediaSourceKind.Spotify,
                MediaActivity.Paused,
                DateTimeOffset.Parse("2026-09-29T18:00:00Z"))
        ]);

        Assert.Equal("spotify", selected?.Id);
    }

    [Fact]
    public void EmptySessionListSelectsNothing()
    {
        Assert.Null(MediaSessionSelector.Select([]));
    }

    [Theory]
    [InlineData("Spotify.exe", MediaSourceKind.Spotify)]
    [InlineData("chrome.exe", MediaSourceKind.Other)]
    [InlineData("vlc.exe", MediaSourceKind.Other)]
    [InlineData("", MediaSourceKind.None)]
    public void ClassifyAcceptsAnyMediaApp(string appId, MediaSourceKind expected)
    {
        Assert.Equal(expected, MediaApp.Classify(appId));
    }

    [Fact]
    public void AccentStaysReadableOnDarkAndLightSurfaces()
    {
        var wallpaper = System.Windows.Media.Color.FromRgb(40, 90, 180);
        var onDark = AccentColor.ForSurface(System.Windows.Media.Colors.Black, wallpaper);
        var onLight = AccentColor.ForSurface(System.Windows.Media.Color.FromRgb(243, 243, 243), wallpaper);

        Assert.True(AccentColor.Contrast(onDark, System.Windows.Media.Colors.Black) >= 3);
        Assert.True(AccentColor.Contrast(onLight, System.Windows.Media.Color.FromRgb(243, 243, 243)) >= 3);
        Assert.True(onDark.B > onDark.R);
        Assert.True(onLight.B > onLight.R);
    }

    [Fact]
    public void ClockFormatsAsMinutesAndSeconds()
    {
        Assert.Equal("01:27", MediaApp.FormatClock(TimeSpan.FromSeconds(87)));
    }

    [Fact]
    public void CompactEventPrefersTheNextTimedAppointment()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(2));
        var events = new[]
        {
            new CalendarEventItem(
                "Festa",
                now.Date,
                now.Date.AddDays(1),
                true),
            new CalendarEventItem(
                "Mattina",
                now.AddHours(-3),
                now.AddHours(-2),
                false),
            new CalendarEventItem(
                "Riunione team design",
                now.AddHours(2),
                now.AddHours(3),
                false)
        };

        var selected = CalendarAgenda.SelectCompact(events, now);

        Assert.Equal("Riunione team design", selected?.Title);
    }

    [Fact]
    public void CompactEventFallsBackToTheFirstWhenAllAreOver()
    {
        var now = new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.FromHours(2));
        var events = new[]
        {
            new CalendarEventItem("Prima", now.AddHours(-5), now.AddHours(-4), false),
            new CalendarEventItem("Seconda", now.AddHours(-3), now.AddHours(-2), false)
        };

        Assert.Equal("Prima", CalendarAgenda.SelectCompact(events, now)?.Title);
    }

    [Fact]
    public void September2026StartsOnTuesdayAndHighlightsToday()
    {
        var today = new DateOnly(2026, 9, 29);
        var cells = CalendarAgenda.BuildMonth(new DateOnly(2026, 9, 1), today);

        Assert.Equal(42, cells.Count);
        Assert.False(cells[0].InMonth);
        Assert.Equal(31, cells[0].Day);
        Assert.True(cells[1].InMonth);
        Assert.Equal(1, cells[1].Day);
        Assert.True(cells[29].IsToday);
        Assert.Equal(29, cells[29].Day);
        Assert.Equal("MAR", CalendarAgenda.Weekday(today));
        var marked = CalendarAgenda.BuildMonth(
            new DateOnly(2026, 9, 1),
            today,
            today,
            [
                new CalendarEventItem(
                    "Riunione",
                    new DateTimeOffset(2026, 9, 30, 14, 30, 0, TimeSpan.FromHours(2)),
                    new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.FromHours(2)),
                    false,
                    "Sala 2")
            ]);
        Assert.True(marked[30].HasEvents);
        Assert.Equal(30, marked[30].Day);
        Assert.True(marked[29].IsSelected);
        Assert.False(marked[29].HasEvents);
        Assert.Equal("Settembre 2026", CalendarAgenda.MonthTitle(new DateOnly(2026, 9, 1)));
    }
}
