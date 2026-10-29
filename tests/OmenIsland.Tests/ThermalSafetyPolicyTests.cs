using OmenIsland.Hardware.Models;
using OmenIsland.Hardware.Safety;

namespace OmenIsland.Tests;

public sealed class ThermalSafetyPolicyTests
{
    private readonly ThermalSafetyPolicy _policy = new();

    [Fact]
    public void HighTemperatureDoesNotOverrideFanMode()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(
            cpu: 94,
            gpu: 90,
            fanMode: FanControlMode.Auto,
            timestamp: now);

        Assert.Equal(
            SafetyAction.None,
            _policy.Evaluate(snapshot, now));
    }

    [Fact]
    public void StaleTelemetryRestoresAutomaticFans()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(
            cpu: null,
            gpu: null,
            fanMode: FanControlMode.Manual,
            timestamp: now,
            isStale: true);

        Assert.Equal(
            SafetyAction.RestoreAutomatic,
            _policy.Evaluate(snapshot, now));
    }

    [Fact]
    public void NormalTelemetryDoesNotChangeFanMode()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(
            cpu: 72,
            gpu: 68,
            fanMode: FanControlMode.Manual,
            timestamp: now);

        Assert.Equal(
            SafetyAction.None,
            _policy.Evaluate(snapshot, now));
    }

    private static HardwareSnapshot Snapshot(
        float? cpu,
        float? gpu,
        FanControlMode fanMode,
        DateTimeOffset timestamp,
        bool isStale = false) =>
        new(
            cpu,
            gpu,
            50,
            50,
            40,
            PerformanceProfile.Default,
            false,
            fanMode,
            50,
            timestamp,
            isStale,
            string.Empty);
}
