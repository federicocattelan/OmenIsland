using OmenIsland.Hardware.Models;

namespace OmenIsland.Hardware.Safety;

public enum SafetyAction
{
    None,
    ForceMaximum,
    RestoreAutomatic
}

public sealed class ThermalSafetyPolicy
{
    public ThermalSafetyPolicy(
        float cpuCriticalCelsius = 92,
        float gpuCriticalCelsius = 87,
        TimeSpan? staleAfter = null)
    {
        CpuCriticalCelsius = cpuCriticalCelsius;
        GpuCriticalCelsius = gpuCriticalCelsius;
        StaleAfter = staleAfter ?? TimeSpan.FromSeconds(10);
    }

    public float CpuCriticalCelsius { get; }

    public float GpuCriticalCelsius { get; }

    public TimeSpan StaleAfter { get; }

    public SafetyAction Evaluate(
        HardwareSnapshot snapshot,
        DateTimeOffset now)
    {
        if (snapshot.IsStale ||
            snapshot.Timestamp == DateTimeOffset.MinValue ||
            now - snapshot.Timestamp > StaleAfter)
        {
            return snapshot.FanMode == FanControlMode.Auto
                ? SafetyAction.None
                : SafetyAction.RestoreAutomatic;
        }

        return SafetyAction.None;
    }
}
