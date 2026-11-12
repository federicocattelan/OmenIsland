using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Principal;
using System.Windows.Input;
using OmenIsland.App.Services;
using OmenIsland.App.Services.Headphones;

namespace OmenIsland.App.Presentation;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly Func<Task> _exitAsync;
    private bool _startWithWindows;
    private string _notice = "La barra resta attiva anche se chiudi questa finestra.";
    private string _calendarNotice = "Nessun account collegato.";
    private PairedDevice? _selectedHeadphones;
    private bool _headphoneAutoConnect;
    private string _headphoneNotice = "";
    private bool _refreshingHeadphones;

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
        RefreshHeadphonesCommand = new AsyncRelayCommand(RefreshHeadphonesAsync);
        _headphoneAutoConnect = settings.Current.HeadphoneAutoConnect;
        if (!string.IsNullOrWhiteSpace(settings.Current.HeadphoneDeviceId))
        {
            _selectedHeadphones = new PairedDevice(
                settings.Current.HeadphoneDeviceId,
                settings.Current.HeadphoneDeviceName);
            PairedHeadphones.Add(_selectedHeadphones);
        }

        _ = RefreshHeadphonesAsync();
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

    public ObservableCollection<PairedDevice> PairedHeadphones { get; } = [];

    public PairedDevice? SelectedHeadphones
    {
        get => _selectedHeadphones;
        set
        {
            // Rebuilding the list makes the ComboBox push null; keep the saved choice.
            if (_refreshingHeadphones)
            {
                return;
            }

            if (SetProperty(ref _selectedHeadphones, value))
            {
                _ = ApplyHeadphonesAsync();
            }
        }
    }

    public bool HeadphoneAutoConnect
    {
        get => _headphoneAutoConnect;
        set
        {
            if (SetProperty(ref _headphoneAutoConnect, value))
            {
                _ = ApplyHeadphonesAsync();
            }
        }
    }

    public string HeadphoneNotice
    {
        get => _headphoneNotice;
        private set => SetProperty(ref _headphoneNotice, value);
    }

    public ICommand RefreshHeadphonesCommand { get; }

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

    private async Task RefreshHeadphonesAsync()
    {
        try
        {
            var devices = await BluetoothDeviceCatalog.ListPairedAsync();
            var selectedId = _selectedHeadphones?.Id;
            _refreshingHeadphones = true;
            try
            {
                PairedHeadphones.Clear();
                foreach (var device in devices)
                {
                    PairedHeadphones.Add(device);
                }
            }
            finally
            {
                _refreshingHeadphones = false;
            }

            var match = devices.FirstOrDefault(device => device.Id == selectedId);
            if (match is not null)
            {
                // Same device, fresh name: no need to reconnect.
                _selectedHeadphones = match;
                OnPropertyChanged(nameof(SelectedHeadphones));
            }
            else if (selectedId is null &&
                     devices.FirstOrDefault(device => BluetoothDeviceCatalog.IsPreferred(device.Name)) is { } preferred)
            {
                SelectedHeadphones = preferred;
            }
            else if (_selectedHeadphones is not null)
            {
                _refreshingHeadphones = true;
                PairedHeadphones.Insert(0, _selectedHeadphones);
                _refreshingHeadphones = false;
                OnPropertyChanged(nameof(SelectedHeadphones));
            }

            HeadphoneNotice = devices.Count == 0
                ? "Nessun dispositivo Bluetooth associato. Associa le cuffie dalle impostazioni di Windows."
                : "";
        }
        catch (Exception exception)
        {
            HeadphoneNotice = $"Bluetooth non disponibile: {exception.Message}";
        }
    }

    private async Task ApplyHeadphonesAsync()
    {
        try
        {
            await Island.Headphones.SelectDeviceAsync(_selectedHeadphones, _headphoneAutoConnect);
        }
        catch (Exception exception)
        {
            HeadphoneNotice = exception.Message;
        }
    }

    private static bool IsProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }
}
