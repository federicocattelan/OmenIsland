namespace OmenIsland.Hardware.Models;

public enum PerformanceProfile
{
    Eco,
    Default,
    Quiet,
    Performance
}

public enum FanControlMode
{
    Auto,
    Manual,
    Max
}

public sealed record HardwareCapabilities(
    bool IsAvailable,
    bool CanWrite,
    string ProductName,
    string BoardId,
    string BiosVersion,
    byte ThermalPolicyVersion,
    IReadOnlyList<PerformanceProfile> SupportedProfiles,
    bool SupportsFanMax,
    bool SupportsManualFan,
    int FanCount,
    string StatusMessage)
{
    public static HardwareCapabilities Unavailable(string message) =>
        new(
            false,
            false,
            "Hardware non rilevato",
            "—",
            "—",
            0,
            Array.Empty<PerformanceProfile>(),
            false,
            false,
            0,
            message);
}

public sealed record HardwareSnapshot(
    float? CpuTemperatureC,
    float? GpuTemperatureC,
    int? CpuFanPercent,
    int? GpuFanPercent,
    int? MemoryLoadPercent,
    PerformanceProfile ActiveProfile,
    bool ProfileObserved,
    FanControlMode FanMode,
    int ManualFanPercent,
    DateTimeOffset Timestamp,
    bool IsStale,
    string StatusMessage)
{
    public static HardwareSnapshot Empty { get; } =
        new(
            null,
            null,
            null,
            null,
            null,
            PerformanceProfile.Default,
            false,
            FanControlMode.Auto,
            50,
            DateTimeOffset.MinValue,
            true,
            "Inizializzazione…");
}
