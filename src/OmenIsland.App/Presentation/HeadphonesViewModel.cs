using System.Windows.Input;
using System.Windows.Threading;
using OmenIsland.App.Services;
using OmenIsland.App.Services.Headphones;

namespace OmenIsland.App.Presentation;

public sealed class HeadphonesViewModel : ObservableObject, IAsyncDisposable
{
    // Same order as the Nothing X app. Dirac Opteo is shown when read but not offered.
    private static readonly EqPreset[] EqCycle =
    [
        EqPreset.Pop, EqPreset.Rock, EqPreset.Classical,
        EqPreset.Electronic, EqPreset.Vocals, EqPreset.Custom
    ];

    private static readonly TimeSpan CustomEqDelay = TimeSpan.FromMilliseconds(250);

    private static readonly SpatialMode[] SpatialCycle =
        [SpatialMode.Off, SpatialMode.Concert, SpatialMode.Theatre];

    private readonly CmfHeadphonesService _service;
    private readonly SettingsStore? _settings;
    private readonly Dispatcher _dispatcher;
    private HeadphoneSnapshot _snapshot = HeadphoneSnapshot.Empty;
    private NoiseMode _lastAncLevel = NoiseMode.AncAdaptive;
    private readonly DispatcherTimer _customEqTimer;
    private CustomEq _customEq;

    public HeadphonesViewModel(CmfHeadphonesService service, SettingsStore? settings = null)
    {
        _service = service;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Slider drags change the bands many times a second; send only once they settle.
        _customEqTimer = new DispatcherTimer(CustomEqDelay, DispatcherPriority.Normal, (_, _) =>
        {
            _customEqTimer!.Stop();
            _ = _service.SetCustomEqAsync(_customEq);
        }, _dispatcher);
        _customEqTimer.Stop();
        _service.Changed += (_, _) =>
        {
            var snapshot = _service.Current;
            if (_dispatcher.CheckAccess())
            {
                Apply(snapshot);
            }
            else
            {
                _dispatcher.BeginInvoke(() => Apply(snapshot));
            }
        };

        SetNoiseCommand = new AsyncRelayCommand<NoiseMode>(
            mode => _service.SetNoiseModeAsync(mode),
            _ => IsConnected);
        SelectAncCommand = new AsyncRelayCommand(
            () => _service.SetNoiseModeAsync(_lastAncLevel),
            () => IsConnected);
        NextEqCommand = new AsyncRelayCommand(() => StepEqAsync(1), () => IsConnected);
        PreviousEqCommand = new AsyncRelayCommand(() => StepEqAsync(-1), () => IsConnected);
        NextSpatialCommand = new AsyncRelayCommand(() => StepSpatialAsync(1), () => IsConnected);
        PreviousSpatialCommand = new AsyncRelayCommand(() => StepSpatialAsync(-1), () => IsConnected);
        ToggleDualCommand = new AsyncRelayCommand(
            () => _service.SetDualConnectionAsync(!DualConnection),
            () => IsConnected);
        ToggleLdacCommand = new AsyncRelayCommand(
            () => _service.SetCodecAsync(IsLdac ? AudioCodec.Aac : AudioCodec.Ldac),
            () => IsConnected);
        ConnectCommand = new AsyncRelayCommand(() => _service.ConnectAsync());
        DisconnectCommand = new AsyncRelayCommand(() => _service.DisconnectAsync());
    }

    public bool IsConnected => _snapshot.IsConnected;

    public bool IsConfigured => _snapshot.Link != HeadphoneLink.NotConfigured;

    public string DeviceName =>
        string.IsNullOrWhiteSpace(_snapshot.DeviceName) ? "Cuffie" : _snapshot.DeviceName;

    public bool HasBattery => _snapshot.Battery is not null;

    public int BatteryPercent => _snapshot.Battery?.Percent ?? 0;

    public double BatteryProgress => BatteryPercent / 100d;

    public string BatteryText => _snapshot.Battery is { } battery ? $"{battery.Percent}%" : "—";

    public bool IsCharging => _snapshot.Battery?.IsCharging ?? false;

    public bool IsBatteryLow => HasBattery && BatteryPercent <= 15 && !IsCharging;

    /// <summary>Off, Transparency or Anc; drives the three noise buttons.</summary>
    public string NoiseCategory => _snapshot.Noise switch
    {
        NoiseMode.Off => "Off",
        NoiseMode.Transparency => "Transparency",
        null => "",
        _ => "Anc"
    };

    public bool IsAncActive => NoiseCategory == "Anc";

    /// <summary>Exact noise mode name, for the ANC level pills.</summary>
    public string NoiseName => _snapshot.Noise?.ToString() ?? "";

    public string EqLabel => _snapshot.Eq switch
    {
        EqPreset.Pop => "Pop",
        EqPreset.Rock => "Rock",
        EqPreset.Classical => "Classica",
        EqPreset.Electronic => "Elettronica",
        EqPreset.Vocals => "Voce",
        EqPreset.Custom => "Custom",
        EqPreset.DiracOpteo => "Dirac",
        _ => "—"
    };

    public bool IsCustomEq => IsConnected && _snapshot.Eq == EqPreset.Custom;

    public int CustomBass
    {
        get => _customEq.Bass;
        set => SetCustomEq(_customEq with { Bass = value });
    }

    public int CustomMid
    {
        get => _customEq.Mid;
        set => SetCustomEq(_customEq with { Mid = value });
    }

    public int CustomTreble
    {
        get => _customEq.Treble;
        set => SetCustomEq(_customEq with { Treble = value });
    }

    public bool DualConnection => _snapshot.DualConnection ?? false;

    public string SpatialLabel => _snapshot.Spatial switch
    {
        SpatialMode.Off => "Off",
        SpatialMode.Concert => "Concerto",
        SpatialMode.Theatre => "Cinema",
        _ => "—"
    };

    public bool IsLdac => _snapshot.Codec == AudioCodec.Ldac;

    public string StatusText => _snapshot.Link switch
    {
        HeadphoneLink.NotConfigured => "Nessuna cuffia selezionata.",
        HeadphoneLink.Connecting => $"Connessione a {DeviceName}…",
        HeadphoneLink.Connected => HasBattery
            ? $"Connesse · {BatteryText}{(IsCharging ? " · in carica" : "")}"
            : "Connesse.",
        HeadphoneLink.Disconnected => string.IsNullOrEmpty(_snapshot.Detail)
            ? $"{DeviceName}: non connesse."
            : _snapshot.Detail,
        _ => string.IsNullOrEmpty(_snapshot.Detail) ? "Cuffie non disponibili." : _snapshot.Detail
    };

    public ICommand SetNoiseCommand { get; }

    public ICommand SelectAncCommand { get; }

    public ICommand NextEqCommand { get; }

    public ICommand PreviousEqCommand { get; }

    public ICommand NextSpatialCommand { get; }

    public ICommand PreviousSpatialCommand { get; }

    public ICommand ToggleDualCommand { get; }

    public ICommand ToggleLdacCommand { get; }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public Task StartAsync()
    {
        var current = _settings?.Current;
        return _service.ConfigureAsync(
            current?.HeadphoneDeviceId,
            current?.HeadphoneAutoConnect ?? true);
    }

    public async Task SelectDeviceAsync(PairedDevice? device, bool autoConnect)
    {
        if (_settings is not null)
        {
            _settings.Current.HeadphoneDeviceId = device?.Id ?? "";
            _settings.Current.HeadphoneDeviceName = device?.Name ?? "";
            _settings.Current.HeadphoneAutoConnect = autoConnect;
            await _settings.SaveAsync();
        }

        await _service.ConfigureAsync(device?.Id, autoConnect);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private Task StepEqAsync(int direction)
    {
        var index = _snapshot.Eq is { } eq ? Array.IndexOf(EqCycle, eq) : -1;
        var next = index < 0
            ? EqCycle[0]
            : EqCycle[(index + direction + EqCycle.Length) % EqCycle.Length];
        return _service.SetEqAsync(next);
    }

    private void SetCustomEq(CustomEq eq)
    {
        if (eq == _customEq || !IsConnected)
        {
            return;
        }

        _customEq = eq;
        RaiseCustomEqChanged();
        _customEqTimer.Stop();
        _customEqTimer.Start();
    }

    private void RaiseCustomEqChanged()
    {
        OnPropertyChanged(nameof(CustomBass));
        OnPropertyChanged(nameof(CustomMid));
        OnPropertyChanged(nameof(CustomTreble));
    }

    private Task StepSpatialAsync(int direction)
    {
        var index = _snapshot.Spatial is { } spatial ? Array.IndexOf(SpatialCycle, spatial) : 0;
        var next = SpatialCycle[(index + direction + SpatialCycle.Length) % SpatialCycle.Length];
        return _service.SetSpatialAsync(next);
    }

    private void Apply(HeadphoneSnapshot snapshot)
    {
        var wasConnected = IsConnected;
        _snapshot = snapshot;
        if (snapshot.Noise is { } noise && noise is not (NoiseMode.Off
                or NoiseMode.Transparency))
        {
            _lastAncLevel = noise;
        }

        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryPercent));
        OnPropertyChanged(nameof(BatteryProgress));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(IsCharging));
        OnPropertyChanged(nameof(IsBatteryLow));
        OnPropertyChanged(nameof(NoiseCategory));
        OnPropertyChanged(nameof(IsAncActive));
        OnPropertyChanged(nameof(NoiseName));
        OnPropertyChanged(nameof(EqLabel));
        OnPropertyChanged(nameof(IsCustomEq));
        // While a drag is waiting to be sent, the local bands are newer than the device's.
        if (!_customEqTimer.IsEnabled && snapshot.CustomEq is { } customEq && customEq != _customEq)
        {
            _customEq = customEq;
            RaiseCustomEqChanged();
        }
        OnPropertyChanged(nameof(DualConnection));
        OnPropertyChanged(nameof(SpatialLabel));
        OnPropertyChanged(nameof(IsLdac));
        OnPropertyChanged(nameof(StatusText));
        if (wasConnected != IsConnected)
        {
            OnPropertyChanged(nameof(IsConnected));
            foreach (var command in new[]
                     {
                         SetNoiseCommand, SelectAncCommand, NextEqCommand, PreviousEqCommand,
                         NextSpatialCommand, PreviousSpatialCommand, ToggleDualCommand, ToggleLdacCommand
                     })
            {
                switch (command)
                {
                    case AsyncRelayCommand plain:
                        plain.RaiseCanExecuteChanged();
                        break;
                    case AsyncRelayCommand<NoiseMode> typed:
                        typed.RaiseCanExecuteChanged();
                        break;
                }
            }
        }
    }
}
