using OmenIsland.Hardware.Models;

namespace OmenIsland.Hardware;

public interface IHardwareController : IAsyncDisposable
{
    HardwareCapabilities Capabilities { get; }

    Task<HardwareCapabilities> InitializeAsync(CancellationToken cancellationToken = default);

    Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task SetProfileAsync(
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SetFanModeAsync(
        FanControlMode mode,
        CancellationToken cancellationToken = default);

    Task SetManualFanPercentAsync(
        int percent,
        CancellationToken cancellationToken = default);

    Task RestoreAutomaticAsync(CancellationToken cancellationToken = default);

    void RememberProfile(PerformanceProfile profile);
}
