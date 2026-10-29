using System.Windows.Input;
using OmenIsland.App.Services;
using OmenIsland.App.Services.Calendar;
using OmenIsland.App.Services.Media;
using OmenIsland.Hardware;
using OmenIsland.Hardware.Models;
using OmenIsland.Hardware.Safety;

namespace OmenIsland.App.Presentation;

public sealed class IslandViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IHardwareController _hardware;
    private readonly IControlConsentService _consent;
    private readonly SettingsStore? _settings;
    private readonly ThermalSafetyPolicy _safetyPolicy;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _monitorTask;
    private string _cpuTemperatureText = "—";
    private string _gpuTemperatureText = "—";
    private string _cpuFanText = "—";
    private string _gpuFanText = "—";
    private string _memoryText = "—";
    private string _fanSummaryText = "— · —";
    private string _activeProfileLabel = "Predefinita";
    private PerformanceProfile? _activeProfile;
    private bool _profileKnown;
    private PerformanceProfile? _observedProfileToSave;
    private FanControlMode _activeFanMode = FanControlMode.Auto;
    private string _fanModeLabel = "Auto";
    private string _statusMessage = "Rilevamento hardware…";
    private string _hardwareName = "HP Victus";
    private string _firmwareInfo = "WMI in attesa";
    private int _manualFanPercent = 50;
    private bool _canControl;
    private bool _supportsManualFan;
    private bool _isBusy;

    public IslandViewModel(
        IHardwareController hardware,
        IControlConsentService consent,
        ThermalSafetyPolicy? safetyPolicy = null,
        SettingsStore? settings = null,
        NowPlayingViewModel? nowPlaying = null,
        CalendarViewModel? calendar = null)
    {
        _hardware = hardware;
        _consent = consent;
        _settings = settings;
        NowPlaying = nowPlaying ?? new NowPlayingViewModel(new NowPlayingService());
        Calendar = calendar ?? new CalendarViewModel(new GoogleCalendarClient(), settings);
        _safetyPolicy = safetyPolicy ?? new ThermalSafetyPolicy();

        if (settings is not null)
        {
            _manualFanPercent = Math.Clamp(
                settings.Current.LastManualFanPercent,
                30,
                100);
            _profileKnown = settings.Current.ProfileChosen;
            if (_profileKnown &&
                Enum.TryParse<PerformanceProfile>(
                    settings.Current.LastProfile,
                    out var profile))
            {
                _activeProfile = profile;
                _activeProfileLabel = ProfileLabel(profile);
            }

            if (Enum.TryParse<FanControlMode>(
                    settings.Current.LastFanMode,
                    out var fanMode) &&
                fanMode != FanControlMode.Manual)
            {
                _activeFanMode = fanMode;
                _fanModeLabel = FanModeText(fanMode);
            }
        }

        SetProfileCommand = new AsyncRelayCommand<PerformanceProfile>(SetProfileAsync);
        SetFanModeCommand = new AsyncRelayCommand<FanControlMode>(
            SetFanModeAsync,
            mode => mode != FanControlMode.Manual);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public string CpuTemperatureText
    {
        get => _cpuTemperatureText;
        private set => SetProperty(ref _cpuTemperatureText, value);
    }

    public string GpuTemperatureText
    {
        get => _gpuTemperatureText;
        private set => SetProperty(ref _gpuTemperatureText, value);
    }

    public string CpuFanText
    {
        get => _cpuFanText;
        private set => SetProperty(ref _cpuFanText, value);
    }

    public string GpuFanText
    {
        get => _gpuFanText;
        private set => SetProperty(ref _gpuFanText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public string FanSummaryText
    {
        get => _fanSummaryText;
        private set => SetProperty(ref _fanSummaryText, value);
    }

    public PerformanceProfile? ActiveProfile
    {
        get => _activeProfile;
        private set
        {
            if (SetProperty(ref _activeProfile, value) && value is not null)
            {
                ActiveProfileLabel = ProfileLabel(value.Value);
            }
        }
    }

    public FanControlMode ActiveFanMode
    {
        get => _activeFanMode;
        private set
        {
            if (SetProperty(ref _activeFanMode, value))
            {
                FanModeLabel = FanModeText(value);
            }
        }
    }

    public event EventHandler? SettingsRequested;

    public string ActiveProfileLabel
    {
        get => _activeProfileLabel;
        private set => SetProperty(ref _activeProfileLabel, value);
    }

    public string FanModeLabel
    {
        get => _fanModeLabel;
        private set => SetProperty(ref _fanModeLabel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string HardwareName
    {
        get => _hardwareName;
        private set => SetProperty(ref _hardwareName, value);
    }

    public string FirmwareInfo
    {
        get => _firmwareInfo;
        private set => SetProperty(ref _firmwareInfo, value);
    }

    public int ManualFanPercent
    {
        get => _manualFanPercent;
        set => SetProperty(
            ref _manualFanPercent,
            Math.Clamp(value, 30, 100));
    }

    public bool CanControl
    {
        get => _canControl;
        private set
        {
            if (SetProperty(ref _canControl, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool SupportsManualFan
    {
        get => _supportsManualFan;
        private set
        {
            if (SetProperty(ref _supportsManualFan, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ICommand SetProfileCommand { get; }

    public ICommand SetFanModeCommand { get; }

    public ICommand RefreshCommand { get; }

    public NowPlayingViewModel NowPlaying { get; }

    public CalendarViewModel Calendar { get; }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            try
            {
                await NowPlaying.StartAsync();
            }
            catch
            {
                // Media sessions are optional if the Windows API is unavailable.
            }

            try
            {
                await Calendar.StartAsync();
            }
            catch
            {
                // Calendar credentials can be added later from settings.
            }

            var capabilities = await _hardware.InitializeAsync(_shutdown.Token);
            HardwareName = capabilities.ProductName;
            FirmwareInfo =
                $"Board {capabilities.BoardId} • BIOS {capabilities.BiosVersion}";
            CanControl = capabilities.IsAvailable && capabilities.CanWrite;
            SupportsManualFan = capabilities.SupportsManualFan;
            StatusMessage = capabilities.StatusMessage;
            RestoreCachedState();

            await RefreshAsync();
            _monitorTask = MonitorAsync(_shutdown.Token);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var snapshot = await _hardware.GetSnapshotAsync(_shutdown.Token);
            ApplySnapshot(snapshot);
            if (_observedProfileToSave is PerformanceProfile observed)
            {
                _observedProfileToSave = null;
                await RememberAsync(observed.ToString(), null, null);
            }

            await ApplySafetyActionAsync(snapshot);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _hardware.RestoreAutomaticAsync();
    }

    public async Task RestoreAutomaticAsync()
    {
        await _hardware.RestoreAutomaticAsync(_shutdown.Token);
        StatusMessage = "Ventole ripristinate su Auto.";
        await RefreshAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        await NowPlaying.DisposeAsync();
        await Calendar.DisposeAsync();
        await _hardware.DisposeAsync();
        _shutdown.Dispose();
        _refreshLock.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshAsync();
        }
    }

    private async Task SetProfileAsync(PerformanceProfile profile)
    {
        if (!await EnsureConsentAsync())
        {
            return;
        }

        await RunControlActionAsync(async () =>
        {
            await _hardware.SetProfileAsync(profile, _shutdown.Token);
            _profileKnown = true;
            await RememberAsync(profile.ToString(), null, null);
        }, $"Profilo {ProfileLabel(profile)} attivo.");
    }

    private async Task SetFanModeAsync(FanControlMode mode)
    {
        if (!await EnsureConsentAsync())
        {
            return;
        }

        await RunControlActionAsync(async () =>
        {
            await _hardware.SetFanModeAsync(mode, _shutdown.Token);
            await RememberAsync(null, mode.ToString(), null);
        }, $"Ventole in modalità {FanModeText(mode)}.");
    }

    private async Task RunControlActionAsync(
        Func<Task> action,
        string successMessage)
    {
        IsBusy = true;
        try
        {
            await action();
            StatusMessage = successMessage;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureConsentAsync()
    {
        if (!CanControl)
        {
            StatusMessage =
                "Per modificare profili e ventole consenti l'accesso dalle impostazioni. La conferma resta valida anche dopo il riavvio del PC.";
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        return await _consent.EnsureConsentAsync();
    }

    private async Task ApplySafetyActionAsync(HardwareSnapshot snapshot)
    {
        if (!CanControl || !_consent.HasAcceptedControl)
        {
            return;
        }

        switch (_safetyPolicy.Evaluate(snapshot, DateTimeOffset.UtcNow))
        {
            case SafetyAction.RestoreAutomatic:
                await _hardware.RestoreAutomaticAsync(_shutdown.Token);
                await RememberAsync(null, FanControlMode.Auto.ToString(), null);
                StatusMessage =
                    "Telemetria assente: ventole ripristinate su Auto.";
                break;
        }
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        CpuTemperatureText = TemperatureText(snapshot.CpuTemperatureC);
        GpuTemperatureText = TemperatureText(snapshot.GpuTemperatureC);
        CpuFanText = PercentText(snapshot.CpuFanPercent);
        GpuFanText = PercentText(snapshot.GpuFanPercent);
        MemoryText = PercentText(snapshot.MemoryLoadPercent);
        FanSummaryText = $"{CpuFanText} · {GpuFanText}";
        if (snapshot.ProfileObserved)
        {
            if (ActiveProfile != snapshot.ActiveProfile)
            {
                _observedProfileToSave = snapshot.ActiveProfile;
            }

            _profileKnown = true;
            ActiveProfile = snapshot.ActiveProfile;
        }
        else if (_profileKnown)
        {
            ActiveProfile = snapshot.ActiveProfile;
        }
        ActiveFanMode = snapshot.FanMode == FanControlMode.Manual
            ? FanControlMode.Auto
            : snapshot.FanMode;

        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            StatusMessage = snapshot.StatusMessage;
        }
    }

    private void RestoreCachedState()
    {
        if (_settings is null ||
            !_settings.Current.ProfileChosen ||
            !Enum.TryParse<PerformanceProfile>(
                _settings.Current.LastProfile,
                out var profile))
        {
            return;
        }

        _hardware.RememberProfile(profile);
    }

    private async Task RememberAsync(
        string? profile,
        string? fanMode,
        int? manualPercent)
    {
        if (_settings is null)
        {
            return;
        }

        if (profile is not null)
        {
            _settings.Current.LastProfile = profile;
            _settings.Current.ProfileChosen = true;
        }

        if (fanMode is not null)
        {
            _settings.Current.LastFanMode = fanMode;
        }

        if (manualPercent is not null)
        {
            _settings.Current.LastManualFanPercent = manualPercent.Value;
        }

        await _settings.SaveAsync(_shutdown.Token);
    }

    private void RaiseCommandStates()
    {
        (SetProfileCommand as AsyncRelayCommand<PerformanceProfile>)
            ?.RaiseCanExecuteChanged();
        (SetFanModeCommand as AsyncRelayCommand<FanControlMode>)
            ?.RaiseCanExecuteChanged();
    }

    private static string TemperatureText(float? temperature) =>
        temperature is null ? "—" : $"{temperature:0}°C";

    private static string PercentText(int? percent) =>
        percent is null ? "—" : $"{percent}%";

    private static string ProfileLabel(PerformanceProfile profile) =>
        profile switch
        {
            PerformanceProfile.Eco => "Eco",
            PerformanceProfile.Default => "Predefinita",
            PerformanceProfile.Quiet => "Silenzioso",
            PerformanceProfile.Performance => "Performance",
            _ => profile.ToString()
        };

    private static string FanModeText(FanControlMode mode) =>
        mode switch
        {
            FanControlMode.Auto => "Auto",
            FanControlMode.Manual => "Manuale",
            FanControlMode.Max => "Max",
            _ => mode.ToString()
        };
}
