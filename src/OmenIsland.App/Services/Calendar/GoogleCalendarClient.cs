using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace OmenIsland.App.Services.Calendar;

public sealed class GoogleCalendarClient
{
    private readonly string _tokenFolder;
    private string _clientId = "";
    private string _clientSecret = "";

    public GoogleCalendarClient(string? tokenFolder = null)
    {
        _tokenFolder = tokenFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenIsland",
            "google-calendar");
    }

    public bool HasStoredCredential() =>
        Directory.Exists(_tokenFolder) &&
        Directory.EnumerateFiles(_tokenFolder).Any();

    public void UseCredentials(string? clientId, string? clientSecret)
    {
        _clientId = clientId?.Trim() ?? "";
        _clientSecret = clientSecret?.Trim() ?? "";
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureCredentials();
        await AuthorizeAsync(cancellationToken);
    }

    public Task DisconnectAsync()
    {
        if (Directory.Exists(_tokenFolder))
        {
            Directory.Delete(_tokenFolder, true);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CalendarEventItem>> GetEventsAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        EnsureCredentials();
        var credential = await AuthorizeAsync(cancellationToken);
        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Omen Island"
        });

        var horizon = DateOnly.FromDateTime(DateTime.Now).AddMonths(6);
        if (to < horizon)
        {
            to = horizon;
        }

        var timeMin = new DateTimeOffset(
            DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var timeMax = new DateTimeOffset(
            DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var calendars = await service.CalendarList.List().ExecuteAsync(cancellationToken);
        var items = new List<CalendarEventItem>();
        if (calendars.Items is null)
        {
            return items;
        }

        foreach (var calendar in calendars.Items)
        {
            if (string.IsNullOrWhiteSpace(calendar.Id) || calendar.Selected == false)
            {
                continue;
            }

            try
            {
                string? pageToken = null;
                do
                {
                    var request = service.Events.List(calendar.Id);
                    request.TimeMinDateTimeOffset = timeMin;
                    request.TimeMaxDateTimeOffset = timeMax;
                    request.SingleEvents = true;
                    request.ShowDeleted = false;
                    request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                    request.MaxResults = 250;
                    request.PageToken = pageToken;
                    var page = await request.ExecuteAsync(cancellationToken);
                    if (page.Items is not null)
                    {
                        foreach (var entry in page.Items)
                        {
                            if (TryMap(entry, out var item))
                            {
                                items.Add(item);
                            }
                        }
                    }

                    pageToken = page.NextPageToken;
                }
                while (!string.IsNullOrEmpty(pageToken));
            }
            catch (Google.GoogleApiException)
            {
                // Some calendars do not allow event lists.
            }
        }

        return items;
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tokenFolder);
        return await Task.Run(
            () => GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = _clientId,
                    ClientSecret = _clientSecret
                },
                [CalendarService.Scope.CalendarReadonly],
                "user",
                cancellationToken,
                new FileDataStore(_tokenFolder, true)),
            cancellationToken);
    }

    private void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(_clientId) ||
            string.IsNullOrWhiteSpace(_clientSecret))
        {
            throw new InvalidOperationException(
                "Omen Island non è ancora registrato su Google. Dopo la registrazione questo pulsante apre il browser: accedi, consenti il calendario e torni qui.");
        }
    }

    private static bool TryMap(Event entry, out CalendarEventItem item)
    {
        item = null!;
        if (entry.Status == "cancelled" || entry.Start is null || entry.End is null)
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(entry.Summary)
            ? "Senza titolo"
            : entry.Summary.Trim();
        var description = CalendarAgenda.PlainDescription(entry.Description);
        if (entry.Start.DateTimeDateTimeOffset is DateTimeOffset start &&
            entry.End.DateTimeDateTimeOffset is DateTimeOffset end)
        {
            item = new CalendarEventItem(title, start, end, false, description);
            return true;
        }

        if (!DateOnly.TryParse(entry.Start.Date, out var startDate) ||
            !DateOnly.TryParse(entry.End.Date, out var endDate))
        {
            return false;
        }

        item = new CalendarEventItem(
            title,
            new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue)),
            new DateTimeOffset(endDate.ToDateTime(TimeOnly.MinValue)),
            true,
            description);
        return true;
    }
}
