using OmenIsland.Hardware.HpWmi;
using OmenIsland.Hardware.Models;
using OmenIsland.Hardware.Telemetry;

namespace OmenIsland.Hardware;

public sealed class HpHardwareController : IHardwareController
{
    private readonly HpWmiBiosClient _wmi;
    private readonly HardwareTelemetryReader _telemetry;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private bool _initialized;
    private bool _isVictus;
    private bool _usesFanLevels;
    private bool _manualLevelApplied;
    private byte _thermalPolicyVersion;
    private PerformanceProfile _activeProfile = PerformanceProfile.Default;
    private FanControlMode _fanMode = FanControlMode.Auto;
    private int _manualFanPercent = 50;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _localProfileSetAt = DateTimeOffset.MinValue;
    private DateTimeOffset _localFanSetAt = DateTimeOffset.MinValue;

    public HpHardwareController(
        HpWmiBiosClient wmi,
        HardwareTelemetryReader telemetry)
    {
        _wmi = wmi;
        _telemetry = telemetry;
    }

    public HardwareCapabilities Capabilities { get; private set; } =
        HardwareCapabilities.Unavailable("Non inizializzato.");

    public async Task<HardwareCapabilities> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return Capabilities;
            }

            try
            {
                await Task.Run(_wmi.Initialize, cancellationToken);

                try
                {
                    await Task.Run(_telemetry.Open, cancellationToken);
                }
                catch
                {
                    // Firmware temperature remains available when sensor access fails.
                }

                var identity = await Task.Run(
                    SystemInfoReader.Read,
                    cancellationToken);
                _isVictus = identity.ProductName.Contains(
                    "Victus",
                    StringComparison.OrdinalIgnoreCase);

                var systemResult = await QueryUnlockedAsync(
                    HpWmiProtocol.GetSystemDesign,
                    HpWmiProtocol.ReadPayload(),
                    128,
                    cancellationToken);
                EnsureSuccess(systemResult, "lettura capacità");

                _thermalPolicyVersion =
                    HpWmiProtocol.GetThermalPolicyVersion(systemResult.Data);
                var softwareFanControl =
                    HpWmiProtocol.HasSoftwareFanControl(systemResult.Data);

                var fanCountResult = await QueryUnlockedAsync(
                    HpWmiProtocol.GetFanCount,
                    HpWmiProtocol.ReadPayload(),
                    4,
                    cancellationToken);
                var fanCount = fanCountResult.IsSuccess &&
                               fanCountResult.Data.Length > 0
                    ? fanCountResult.Data[0]
                    : 0;

                var fanLevelsResult = await QueryUnlockedAsync(
                    HpWmiProtocol.GetFanLevel,
                    HpWmiProtocol.ReadPayload(),
                    128,
                    cancellationToken);
                _usesFanLevels = fanLevelsResult.IsSuccess &&
                                 fanLevelsResult.Data.Length >= 2 &&
                                 (fanLevelsResult.Data[0] > 0 ||
                                  fanLevelsResult.Data[1] > 0);

                var legacyFanReadable = false;
                if (!_usesFanLevels)
                {
                    var legacyFan = await QueryUnlockedAsync(
                        HpWmiProtocol.GetLegacyFanSpeed,
                        HpWmiProtocol.LegacyFanQuery(0),
                        4,
                        cancellationToken);
                    legacyFanReadable = legacyFan.IsSuccess;
                }

                var maxFanResult = await QueryUnlockedAsync(
                    HpWmiProtocol.GetMaxFan,
                    HpWmiProtocol.ReadPayload(),
                    4,
                    cancellationToken);
                if (maxFanResult.IsSuccess &&
                    maxFanResult.Data.Length > 0 &&
                    maxFanResult.Data[0] == 1)
                {
                    _fanMode = FanControlMode.Max;
                }

                var profiles = new[]
                {
                    PerformanceProfile.Eco,
                    PerformanceProfile.Default,
                    PerformanceProfile.Quiet,
                    PerformanceProfile.Performance
                };

                var canWrite = _wmi.IsAdministrator;
                var status = canWrite
                    ? "HP WMI pronto."
                    : "Sola lettura: riavvia come amministratore per i controlli.";

                Capabilities = new HardwareCapabilities(
                    true,
                    canWrite,
                    identity.ProductName,
                    identity.BoardId,
                    identity.BiosVersion,
                    _thermalPolicyVersion,
                    profiles,
                    fanCount > 0 || maxFanResult.IsSuccess,
                    softwareFanControl || _usesFanLevels || legacyFanReadable,
                    fanCount,
                    status);
                _initialized = true;
            }
            catch (Exception exception)
            {
                Capabilities = HardwareCapabilities.Unavailable(
                    $"HP WMI non disponibile: {exception.Message}");
                _initialized = true;
            }

            return Capabilities;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<HardwareSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        float? cpuTemperature = null;
        float? gpuTemperature = null;
        int? cpuFanPercent = null;
        int? gpuFanPercent = null;
        var status = Capabilities.IsAvailable
            ? string.Empty
            : Capabilities.StatusMessage;

        try
        {
            var temperatures = await Task.Run(
                _telemetry.Read,
                cancellationToken);
            cpuTemperature = temperatures.CpuCelsius;
            gpuTemperature = temperatures.GpuCelsius;
        }
        catch (Exception exception)
        {
            status = $"Telemetria parziale: {exception.Message}";
        }

        if (cpuTemperature is null && Capabilities.IsAvailable)
        {
            try
            {
                cpuTemperature = await ReadFirmwareTemperatureAsync(
                    cancellationToken);
            }
            catch
            {
                // The sensor reading above remains the primary temperature.
            }
        }

        if (Capabilities.IsAvailable)
        {
            try
            {
                await SendHeartbeatIfNeededAsync(cancellationToken);
                (cpuFanPercent, gpuFanPercent) =
                    await ReadFanPercentsAsync(cancellationToken);
            }
            catch
            {
                // Temperature telemetry remains useful when a fan query is unsupported.
            }
        }

        var observedProfile = await TryReadActiveProfileAsync(cancellationToken);
        if (observedProfile is not null)
        {
            _activeProfile = observedProfile.Value;
        }

        var observedFanMode = await TryReadFanModeAsync(cancellationToken);
        if (observedFanMode is not null)
        {
            _fanMode = observedFanMode.Value;
        }

        var now = DateTimeOffset.UtcNow;
        var isStale = cpuTemperature is null && gpuTemperature is null;
        var memoryLoad = MemoryStatusReader.ReadLoadPercent();

        return new HardwareSnapshot(
            cpuTemperature,
            gpuTemperature,
            cpuFanPercent,
            gpuFanPercent,
            memoryLoad,
            _activeProfile,
            observedProfile is not null,
            _fanMode,
            _manualFanPercent,
            now,
            isStale,
            status);
    }

    public async Task SetProfileAsync(
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        await EnsureControllableAsync(cancellationToken);

        if (!Capabilities.SupportedProfiles.Contains(profile))
        {
            throw new NotSupportedException(
                $"Il profilo {profile} non è supportato dal firmware.");
        }

        var result = await ExecuteAsync(
            HpWmiProtocol.SetPerformanceMode,
            HpWmiProtocol.PerformancePayload(
                profile,
                _thermalPolicyVersion,
                _isVictus),
            0,
            cancellationToken);
        EnsureSuccess(result, $"impostazione profilo {profile}");
        _activeProfile = profile;
        _localProfileSetAt = DateTimeOffset.UtcNow;
    }

    public async Task SetFanModeAsync(
        FanControlMode mode,
        CancellationToken cancellationToken = default)
    {
        await EnsureControllableAsync(cancellationToken);

        switch (mode)
        {
            case FanControlMode.Auto:
                await SetMaxFanCoreAsync(false, cancellationToken);
                if (_manualLevelApplied && _usesFanLevels)
                {
                    await SetFanLevelsCoreAsync(0, 0, cancellationToken);
                }

                _manualLevelApplied = false;
                break;

            case FanControlMode.Max:
                if (!Capabilities.SupportsFanMax)
                {
                    throw new NotSupportedException(
                        "La modalità ventole Max non è supportata.");
                }

                await SetMaxFanCoreAsync(true, cancellationToken);
                break;

            case FanControlMode.Manual:
                if (!Capabilities.SupportsManualFan)
                {
                    throw new NotSupportedException(
                        "Il controllo manuale non è supportato dal firmware.");
                }

                await SetMaxFanCoreAsync(false, cancellationToken);
                await SetManualFanPercentCoreAsync(
                    _manualFanPercent,
                    cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        _fanMode = mode;
        _localFanSetAt = DateTimeOffset.UtcNow;
    }

    public async Task SetManualFanPercentAsync(
        int percent,
        CancellationToken cancellationToken = default)
    {
        await EnsureControllableAsync(cancellationToken);

        if (!Capabilities.SupportsManualFan)
        {
            throw new NotSupportedException(
                "Il controllo manuale non è supportato dal firmware.");
        }

        await SetManualFanPercentCoreAsync(percent, cancellationToken);
        _fanMode = FanControlMode.Manual;
    }

    public async Task RestoreAutomaticAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_initialized ||
            !Capabilities.IsAvailable ||
            !Capabilities.CanWrite)
        {
            return;
        }

        try
        {
            await SetFanModeAsync(FanControlMode.Auto, cancellationToken);
        }
        catch
        {
            // Shutdown and suspend restoration is best effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreAutomaticAsync();
        _telemetry.Dispose();
        _wmi.Dispose();
        _operationLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private async Task EnsureControllableAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (!Capabilities.IsAvailable)
        {
            throw new NotSupportedException(Capabilities.StatusMessage);
        }

        if (!Capabilities.CanWrite)
        {
            throw new UnauthorizedAccessException(
                "Avvia Omen Island come amministratore per modificare il firmware.");
        }
    }

    private async Task SetMaxFanCoreAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            HpWmiProtocol.SetMaxFan,
            HpWmiProtocol.MaxFanPayload(enabled),
            0,
            cancellationToken);
        EnsureSuccess(result, enabled
            ? "attivazione ventole Max"
            : "disattivazione ventole Max");
    }

    private async Task SetManualFanPercentCoreAsync(
        int percent,
        CancellationToken cancellationToken)
    {
        var safePercent = FanPercentMapper.ClampManualPercent(percent);
        var cpuLevel = FanPercentMapper.ToRawLevel(
            safePercent,
            FanPercentMapper.CpuMaximumLevel);
        var gpuLevel = FanPercentMapper.ToRawLevel(
            safePercent,
            FanPercentMapper.GpuMaximumLevel);

        await SetFanLevelsCoreAsync(cpuLevel, gpuLevel, cancellationToken);
        _manualFanPercent = safePercent;
        _manualLevelApplied = true;
    }

    public void RememberProfile(PerformanceProfile profile)
    {
        _activeProfile = profile;
    }

    private async Task SetFanLevelsCoreAsync(
        byte cpuLevel,
        byte gpuLevel,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            HpWmiProtocol.SetFanSpeed,
            HpWmiProtocol.FanSpeedPayload(cpuLevel, gpuLevel),
            0,
            cancellationToken);
        EnsureSuccess(result, "impostazione velocità ventole");
    }

    private async Task<(int? Cpu, int? Gpu)> ReadFanPercentsAsync(
        CancellationToken cancellationToken)
    {
        if (_usesFanLevels)
        {
            var fanResult = await ExecuteAsync(
                HpWmiProtocol.GetFanLevel,
                HpWmiProtocol.ReadPayload(),
                128,
                cancellationToken);

            if (fanResult.IsSuccess && fanResult.Data.Length >= 2)
            {
                return (
                    FanPercentMapper.FromRawLevel(
                        fanResult.Data[0],
                        FanPercentMapper.CpuMaximumLevel),
                    FanPercentMapper.FromRawLevel(
                        fanResult.Data[1],
                        FanPercentMapper.GpuMaximumLevel));
            }
        }

        var cpuRpm = await ReadLegacyRpmAsync(0, cancellationToken);
        var gpuRpm = await ReadLegacyRpmAsync(1, cancellationToken);
        return (
            cpuRpm is null
                ? null
                : FanPercentMapper.FromRpm(cpuRpm.Value, FanPercentMapper.CpuMaximumRpm),
            gpuRpm is null
                ? null
                : FanPercentMapper.FromRpm(gpuRpm.Value, FanPercentMapper.GpuMaximumRpm));
    }

    private async Task<int?> ReadLegacyRpmAsync(
        byte fanIndex,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            HpWmiProtocol.GetLegacyFanSpeed,
            HpWmiProtocol.LegacyFanQuery(fanIndex),
            4,
            cancellationToken);

        return result.IsSuccess
            ? HpWmiProtocol.ReadLegacyRpm(result.Data)
            : null;
    }

    private async Task<float?> ReadFirmwareTemperatureAsync(
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            HpWmiProtocol.GetFirmwareTemperature,
            HpWmiProtocol.FirmwareTemperaturePayload(),
            4,
            cancellationToken);

        var celsius = result.IsSuccess
            ? HpWmiProtocol.ReadFirmwareCelsius(result.Data)
            : null;
        return celsius;
    }

    private async Task SendHeartbeatIfNeededAsync(
        CancellationToken cancellationToken)
    {
        if (_fanMode == FanControlMode.Auto ||
            DateTimeOffset.UtcNow - _lastHeartbeat < TimeSpan.FromSeconds(90))
        {
            return;
        }

        var result = await ExecuteAsync(
            HpWmiProtocol.GetFanCount,
            HpWmiProtocol.ReadPayload(),
            4,
            cancellationToken);

        if (result.IsSuccess)
        {
            _lastHeartbeat = DateTimeOffset.UtcNow;
        }
    }

    private async Task<FanControlMode?> TryReadFanModeAsync(
        CancellationToken cancellationToken)
    {
        if (!Capabilities.IsAvailable ||
            DateTimeOffset.UtcNow - _localFanSetAt < TimeSpan.FromSeconds(2))
        {
            return null;
        }

        try
        {
            var result = await ExecuteAsync(
                HpWmiProtocol.GetMaxFan,
                HpWmiProtocol.ReadPayload(),
                4,
                cancellationToken);

            if (!result.IsSuccess || result.Data.Length == 0)
            {
                return null;
            }

            return result.Data[0] switch
            {
                1 => FanControlMode.Max,
                0 => FanControlMode.Auto,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<PerformanceProfile?> TryReadActiveProfileAsync(
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _localProfileSetAt < TimeSpan.FromSeconds(2))
        {
            return null;
        }

        try
        {
            var value = await Task.Run(EcThermalProfileReader.TryRead, cancellationToken);
            return value is null
                ? null
                : HpWmiProtocol.FromFirmwareByte(value.Value, isVictus: true);
        }
        catch
        {
            return null;
        }
    }

    private async Task<HpWmiResult> ExecuteAsync(
        byte commandType,
        byte[] payload,
        int outputSize,
        CancellationToken cancellationToken,
        uint command = HpWmiProtocol.GamingCommand)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await QueryUnlockedAsync(
                command,
                commandType,
                payload,
                outputSize,
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private Task<HpWmiResult> QueryUnlockedAsync(
        byte commandType,
        byte[] payload,
        int outputSize,
        CancellationToken cancellationToken) =>
        QueryUnlockedAsync(
            HpWmiProtocol.GamingCommand,
            commandType,
            payload,
            outputSize,
            cancellationToken);

    private Task<HpWmiResult> QueryUnlockedAsync(
        uint command,
        byte commandType,
        byte[] payload,
        int outputSize,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _wmi.Execute(command, commandType, payload, outputSize),
            cancellationToken);

    private static void EnsureSuccess(HpWmiResult result, string operation)
    {
        if (!result.IsSuccess)
        {
            throw new HpWmiException(
                $"Errore HP WMI {result.ReturnCode} durante {operation}.");
        }
    }
}
