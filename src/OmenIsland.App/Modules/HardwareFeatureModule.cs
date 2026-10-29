using OmenIsland.App.Presentation;

namespace OmenIsland.App.Modules;

public sealed class HardwareFeatureModule : IFeatureModule
{
    private readonly IslandViewModel _viewModel;

    public HardwareFeatureModule(IslandViewModel viewModel)
    {
        _viewModel = viewModel;
        CompactMetrics =
        [
            new(
                "cpu-temperature",
                "CPU",
                "\uE950",
                () => _viewModel.CpuTemperatureText),
            new(
                "gpu-temperature",
                "GPU",
                "\uE7F4",
                () => _viewModel.GpuTemperatureText),
            new(
                "fan-speed",
                "Ventole",
                "\uE9CA",
                () => _viewModel.CpuFanText)
        ];
    }

    public string Id => "hardware";

    public string DisplayName => "Hardware";

    public int Order => 0;

    public IReadOnlyList<CompactMetric> CompactMetrics { get; }

    public object ExpandedViewModel => _viewModel;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _viewModel.InitializeAsync();
}
