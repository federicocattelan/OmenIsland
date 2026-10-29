using System.Windows;
using Microsoft.Win32;
using OmenIsland.App.Modules;
using OmenIsland.App.Presentation;
using OmenIsland.App.Services;
using OmenIsland.App.Views;
using OmenIsland.Hardware;
using OmenIsland.Hardware.HpWmi;
using OmenIsland.Hardware.Telemetry;

namespace OmenIsland.App;

public partial class App : System.Windows.Application
{
    private const string ShowSettingsEventName = @"Local\OmenIsland.ShowSettings";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsEvent;
    private CancellationTokenSource? _showSettingsLoop;
    private IslandViewModel? _viewModel;
    private IslandWindow? _window;
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _trayIcon;
    private SettingsStore? _settings;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var background = e.Args.Any(argument =>
            argument.Equals(
                WindowsIntegration.BackgroundArgument,
                StringComparison.OrdinalIgnoreCase));
        var kickElevated = e.Args.Any(argument =>
            argument.Equals(
                WindowsIntegration.StartElevatedArgument,
                StringComparison.OrdinalIgnoreCase));
        if (kickElevated && !WindowsIntegration.IsProcessAdministrator())
        {
            var started = await Task.Run(WindowsIntegration.TryRunStartupTask);
            if (started)
            {
                Shutdown();
                return;
            }

            background = true;
        }

        _singleInstanceMutex = new Mutex(
            true,
            @"Local\OmenIsland.SingleInstance",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalSettings();
            Shutdown();
            return;
        }

        _showSettingsEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowSettingsEventName);
        _showSettingsLoop = new CancellationTokenSource();
        _ = ListenForSettingsRequestsAsync(_showSettingsLoop.Token);

        try
        {
            WindowsIntegration.EnsureStartMenuShortcut();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Omen Island",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _settings = new SettingsStore();
        await _settings.LoadAsync();
        if (_settings.Current.StartWithWindows)
        {
            WindowsIntegration.SetRunAtStartup(true);
        }

        var hardware = new HpHardwareController(
            new HpWmiBiosClient(),
            new HardwareTelemetryReader());
        var consent = new ControlConsentService(_settings);
        _viewModel = new IslandViewModel(hardware, consent, settings: _settings);
        _viewModel.SettingsRequested += (_, _) => ShowSettings();

        var modules = new ModuleRegistry();
        modules.Register(new HardwareFeatureModule(_viewModel));

        _window = new IslandWindow(_viewModel);
        _window.SettingsRequested += (_, _) => ShowSettings();
        _window.Show();

        _trayIcon = new TrayIconService(
            _window,
            _viewModel,
            ShowSettings,
            ExitAsync);
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        if (!background)
        {
            ShowSettings();
        }

        try
        {
            await modules.InitializeAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Omen Island — inizializzazione",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void ShowSettings()
    {
        if (_settings is null || _viewModel is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow
                {
                    DataContext = new SettingsViewModel(
                        _settings,
                        _viewModel,
                        ExitAsync)
                };
            }

            _settingsWindow.Show();
            _settingsWindow.Activate();
        });
    }

    public async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.DisposeAsync();
            }
        }
        finally
        {
            _showSettingsLoop?.Cancel();
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _trayIcon?.Dispose();
            _trayIcon = null;

            if (_settingsWindow is not null)
            {
                _settingsWindow.PermitClose();
                _settingsWindow.Close();
                _settingsWindow = null;
            }

            if (_window is not null)
            {
                _window.PermitClose();
                _window.Close();
            }

            _showSettingsEvent?.Dispose();
            _showSettingsEvent = null;

            if (_singleInstanceMutex is not null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            Shutdown();
        }
    }

    private static void SignalSettings()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(
                    ShowSettingsEventName,
                    out var showSettings))
            {
                using (showSettings)
                {
                    showSettings.Set();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private async Task ListenForSettingsRequestsAsync(CancellationToken cancellationToken)
    {
        var showSettingsEvent = _showSettingsEvent;
        if (showSettingsEvent is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var signaled = await Task.Run(
                () => showSettingsEvent.WaitOne(TimeSpan.FromMilliseconds(400)));
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (signaled)
            {
                ShowSettings();
            }
        }
    }

    private async void SystemEvents_PowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                await _viewModel.RestoreAutomaticAsync();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                await _viewModel.RefreshAsync();
            }
        }
        catch
        {
            // Power transitions must not terminate the overlay.
        }
    }
}
