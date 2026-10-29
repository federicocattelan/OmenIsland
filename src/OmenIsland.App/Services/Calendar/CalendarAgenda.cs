using System.Globalization;

namespace OmenIsland.App.Services.Calendar;

public sealed record CalendarEventItem(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Description = "");

public sealed record CalendarCell(
    DateOnly Date,
    int Day,
    bool InMonth,
    bool IsToday,
    bool HasEvents,
    bool IsSelected);

public static class CalendarAgenda
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string MonthTitle(DateOnly month)
    {
        var text = new DateTime(month.Year, month.Month, 1).ToString("MMMM yyyy", Italian);
        return text.Length == 0
            ? text
            : char.ToUpper(text[0], Italian) + text[1..];
    }

    public static string Weekday(DateOnly day) =>
        Italian.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek).ToUpper(Italian);

    public static CalendarEventItem? SelectCompact(
        IReadOnlyList<CalendarEventItem> events,
        DateTimeOffset now)
    {
        var today = events
            .Where(item => OverlapsDay(item, DateOnly.FromDateTime(now.LocalDateTime)))
            .OrderBy(item => item.Start)
            .ToList();
        if (today.Count == 0)
        {
            return null;
        }

        var upcomingTimed = today.FirstOrDefault(item => !item.AllDay && item.End > now);
        if (upcomingTimed is not null)
        {
            return upcomingTimed;
        }

        var allDay = today.FirstOrDefault(item => item.AllDay && item.End > now);
        return allDay ?? today[0];
    }

    public static IReadOnlyList<CalendarEventItem> EventsOn(
        IReadOnlyList<CalendarEventItem> events,
        DateOnly day) =>
        events
            .Where(item => OverlapsDay(item, day))
            .OrderBy(item => item.AllDay ? 0 : 1)
            .ThenBy(item => item.Start)
            .ToList();

    public static IReadOnlyList<CalendarCell> BuildMonth(
        DateOnly month,
        DateOnly today,
        DateOnly? selected = null,
        IReadOnlyList<CalendarEventItem>? events = null)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var chosen = selected ?? today;
        var source = events ?? [];
        var cells = new CalendarCell[42];
        for (var index = 0; index < cells.Length; index++)
        {
            var date = start.AddDays(index);
            cells[index] = new CalendarCell(
                date,
                date.Day,
                date.Month == month.Month,
                date == today,
                source.Any(item => OverlapsDay(item, date)),
                date == chosen);
        }

        return cells;
    }

    public static (DateOnly From, DateOnly To) FetchRange(DateOnly month, DateOnly today)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var from = first.AddDays(-offset);
        var to = from.AddDays(41);
        if (today < from)
        {
            from = today;
        }

        if (today > to)
        {
            to = today;
        }

        return (from, to);
    }

    public static string FormatTime(CalendarEventItem item) =>
        item.AllDay
            ? "Tutto il giorno"
            : $"{item.Start.ToLocalTime():HH:mm}–{item.End.ToLocalTime():HH:mm}";

    public static string PlainDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = System.Text.RegularExpressions.Regex.Replace(
            value,
            "<br\\s*/?>|</p>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    public static string SelectedHeading(DateOnly day, DateOnly today) =>
        day == today
            ? "OGGI"
            : day.ToString("d MMMM", Italian);

    private static bool OverlapsDay(CalendarEventItem item, DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        var localStart = item.Start.LocalDateTime;
        var localEnd = item.End.LocalDateTime;
        return localStart < end && localEnd > start;
    }
}
