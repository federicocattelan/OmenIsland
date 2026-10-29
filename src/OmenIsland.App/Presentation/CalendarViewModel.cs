using System.Windows.Input;
using System.Windows.Threading;
using OmenIsland.App.Services;
using OmenIsland.App.Services.Calendar;

namespace OmenIsland.App.Presentation;

public sealed class CalendarEventView : ObservableObject
{
    private bool _isExpanded;

    public CalendarEventView(string title, string timeText, string description)
    {
        Title = title;
        TimeText = timeText;
        Description = description;
    }

    public string Title { get; }

    public string TimeText { get; }

    public string Description { get; }

    public bool CanExpand =>
        Description.Contains('\n') || Description.Length > 110;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(DescriptionToggleText));
                OnPropertyChanged(nameof(ShownDescription));
            }
        }
    }

    public string DescriptionToggleText => IsExpanded ? "Mostra meno" : "Mostra altro";

    public string ShownDescription
    {
        get
        {
            if (!CanExpand || IsExpanded || Description.Length <= 110)
            {
                return Description;
            }

            var excerpt = Description.Length <= 110
                ? Description
                : Description[..110];
            var line = excerpt.IndexOf('\n');
            if (line >= 0)
            {
                excerpt = excerpt[..line];
            }

            return excerpt.TrimEnd() + "…";
        }
    }
}

public sealed class CalendarViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GoogleCalendarClient _client;
    private readonly SettingsStore? _settings;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyList<CalendarEventItem> _events = [];
    private DateOnly _displayMonth;
    private DateOnly _selected;
    private Task? _loop;
    private string _weekdayText = "";
    private string _dayNumber = "";
    private string _monthLabel = "";
    private string _compactEventTitle = "";
    private string _compactEventTime = "";
    private string _statusText = "";
    private string _selectedHeading = "OGGI";
    private bool _isConnected;
    private bool _hasCompactEvent;
    private IReadOnlyList<CalendarCell> _cells = [];
    private IReadOnlyList<CalendarEventView> _todayEvents = [];

    public CalendarViewModel(GoogleCalendarClient client, SettingsStore? settings)
    {
        _client = client;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var today = DateOnly.FromDateTime(DateTime.Now);
        _selected = today;
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        PreviousMonthCommand = new AsyncRelayCommand(StepMonthAsync(-1));
        NextMonthCommand = new AsyncRelayCommand(StepMonthAsync(1));
        SelectDayCommand = new RelayCommand(SelectDay);
        ToggleDescriptionCommand = new RelayCommand(ToggleDescription);
        ApplyLocal(today);
    }

    public string WeekdayText
    {
        get => _weekdayText;
        private set => SetProperty(ref _weekdayText, value);
    }

    public string DayNumber
    {
        get => _dayNumber;
        private set => SetProperty(ref _dayNumber, value);
    }

    public string MonthLabel
    {
        get => _monthLabel;
        private set => SetProperty(ref _monthLabel, value);
    }

    public string CompactEventTitle
    {
        get => _compactEventTitle;
        private set => SetProperty(ref _compactEventTitle, value);
    }

    public string CompactEventTime
    {
        get => _compactEventTime;
        private set => SetProperty(ref _compactEventTime, value);
    }

    public bool HasCompactEvent
    {
        get => _hasCompactEvent;
        private set => SetProperty(ref _hasCompactEvent, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IReadOnlyList<CalendarCell> Cells
    {
        get => _cells;
        private set => SetProperty(ref _cells, value);
    }

    public IReadOnlyList<CalendarEventView> TodayEvents
    {
        get => _todayEvents;
        private set => SetProperty(ref _todayEvents, value);
    }

    public bool HasSelectedEvents => TodayEvents.Count > 0;

    public string SelectedHeading
    {
        get => _selectedHeading;
        private set => SetProperty(ref _selectedHeading, value);
    }

    public ICommand PreviousMonthCommand { get; }

    public ICommand NextMonthCommand { get; }

    public ICommand SelectDayCommand { get; }

    public ICommand ToggleDescriptionCommand { get; }

    public event EventHandler? LayoutChanged;

    public Task StartAsync()
    {
        ApplyStoredCredentials();
        IsConnected = _client.HasStoredCredential() &&
                      !string.IsNullOrWhiteSpace(GoogleSignIn.ClientId);
        _loop = PollAsync(_shutdown.Token);
        return RefreshAsync();
    }

    public async Task ConnectAsync()
    {
        ApplyStoredCredentials();
        await _client.ConnectAsync(_shutdown.Token);
        IsConnected = true;
        StatusText = "";
        await RefreshAsync();
    }

    public async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
        IsConnected = false;
        _events = [];
        StatusText = "";
        ApplyLocal(DateOnly.FromDateTime(DateTime.Now));
    }

    public Task RefreshAsync() => LoadAsync(DateOnly.FromDateTime(DateTime.Now));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    public void ResetToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _selected = today;
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        ApplyLocal(today);
    }

    private void ToggleDescription(object? parameter)
    {
        if (parameter is not CalendarEventView item || !item.CanExpand)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectDay(object? parameter)
    {
        if (parameter is not CalendarCell cell)
        {
            return;
        }

        _selected = cell.Date;
        ApplyLocal(DateOnly.FromDateTime(DateTime.Now));
    }

    private void ApplyStoredCredentials() =>
        _client.UseCredentials(GoogleSignIn.ClientId, GoogleSignIn.ClientSecret);

    private Func<Task> StepMonthAsync(int months) =>
        async () =>
        {
            _displayMonth = _displayMonth.AddMonths(months);
            await RefreshAsync();
        };

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await LoadAsync(DateOnly.FromDateTime(DateTime.Now));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync(DateOnly today)
    {
        if (!IsConnected)
        {
            Publish(today, []);
            return;
        }

        var range = CalendarAgenda.FetchRange(_displayMonth, today);
        try
        {
            var events = await _client.GetEventsAsync(
                range.From,
                range.To,
                _shutdown.Token);
            Publish(today, events);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RunOnUi(() => StatusText = exception.Message);
        }
    }

    private void Publish(DateOnly today, IReadOnlyList<CalendarEventItem> events)
    {
        RunOnUi(() =>
        {
            _events = events;
            StatusText = "";
            ApplyLocal(today);
        });
    }

    private void ApplyLocal(DateOnly today)
    {
        WeekdayText = CalendarAgenda.Weekday(today);
        DayNumber = today.Day.ToString();
        MonthLabel = CalendarAgenda.MonthTitle(_displayMonth);
        SelectedHeading = CalendarAgenda.SelectedHeading(_selected, today);
        Cells = CalendarAgenda.BuildMonth(_displayMonth, today, _selected, _events);
        var compact = IsConnected
            ? CalendarAgenda.SelectCompact(_events, DateTimeOffset.Now)
            : null;
        HasCompactEvent = compact is not null;
        CompactEventTitle = compact?.Title ?? "";
        CompactEventTime = compact is null ? "" : CalendarAgenda.FormatTime(compact);
        TodayEvents = IsConnected
            ? CalendarAgenda.EventsOn(_events, _selected)
                .Select(item => new CalendarEventView(
                    item.Title,
                    CalendarAgenda.FormatTime(item),
                    item.Description))
                .ToList()
            : [];
        OnPropertyChanged(nameof(HasSelectedEvents));
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }
}
