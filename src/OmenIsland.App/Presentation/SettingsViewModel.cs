using System.ComponentModel;
using System.Security.Principal;
using System.Windows.Input;
using OmenIsland.App.Services;

namespace OmenIsland.App.Presentation;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly Func<Task> _exitAsync;
    private bool _startWithWindows;
    private string _notice = "La barra resta attiva anche se chiudi questa finestra.";
    private string _calendarNotice = "Nessun account collegato.";

    public SettingsViewModel(
        SettingsStore settings,
        IslandViewModel island,
        Func<Task> exitAsync)
    {
        _settings = settings;
        _exitAsync = exitAsync;
        Island = island;
        _startWithWindows = settings.Current.StartWithWindows;
        IsAdministrator = IsProcessAdministrator();

        ExitCommand = new AsyncRelayCommand(exitAsync);
        ElevateCommand = new AsyncRelayCommand(ElevateAsync);
        ConnectCalendarCommand = new AsyncRelayCommand(ConnectCalendarAsync);
        DisconnectCalendarCommand = new AsyncRelayCommand(DisconnectCalendarAsync);
        island.Calendar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CalendarViewModel.IsConnected)
                or nameof(CalendarViewModel.StatusText))
            {
                OnPropertyChanged(nameof(IsCalendarConnected));
                if (!string.IsNullOrWhiteSpace(island.Calendar.StatusText))
                {
                    CalendarNotice = island.Calendar.StatusText;
                }
            }
        };
    }

    public IslandViewModel Island { get; }

    public bool IsAdministrator { get; }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value))
            {
                return;
            }

            _settings.Current.StartWithWindows = value;
            WindowsIntegration.SetRunAtStartup(value);
            _ = _settings.SaveAsync();
            Notice = value
                ? "Omen Island si aprirà da solo all'accesso a Windows."
                : "Apri Omen Island dal menu Start quando ti serve.";
        }
    }

    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public string CalendarNotice
    {
        get => _calendarNotice;
        private set => SetProperty(ref _calendarNotice, value);
    }

    public bool IsCalendarConnected => Island.Calendar.IsConnected;

    public ICommand ExitCommand { get; }

    public ICommand ElevateCommand { get; }

    public ICommand ConnectCalendarCommand { get; }

    public ICommand DisconnectCalendarCommand { get; }

    private async Task ElevateAsync()
    {
        try
        {
            WindowsIntegration.RestartElevated();
        }
        catch (Win32Exception)
        {
            return;
        }

        await _exitAsync();
    }

    private async Task ConnectCalendarAsync()
    {
        try
        {
            await Island.Calendar.ConnectAsync();
            CalendarNotice = "Account Google collegato.";
            OnPropertyChanged(nameof(IsCalendarConnected));
        }
        catch (Exception exception)
        {
            CalendarNotice = exception.Message;
        }
    }

    private async Task DisconnectCalendarAsync()
    {
        await Island.Calendar.DisconnectAsync();
        CalendarNotice = "Google Calendar scollegato.";
        OnPropertyChanged(nameof(IsCalendarConnected));
    }

    private static bool IsProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }
}
