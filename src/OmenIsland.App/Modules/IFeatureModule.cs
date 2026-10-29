namespace OmenIsland.App.Modules;

/// <summary>
/// Extension point for future cards such as calendar and media controls.
/// A module supplies compact metrics and an expanded presentation model
/// without coupling itself to IslandWindow.
/// </summary>
public interface IFeatureModule
{
    string Id { get; }

    string DisplayName { get; }

    int Order { get; }

    IReadOnlyList<CompactMetric> CompactMetrics { get; }

    object ExpandedViewModel { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed record CompactMetric(
    string Id,
    string Label,
    string IconGlyph,
    Func<string> ValueProvider);

public sealed class ModuleRegistry
{
    private readonly List<IFeatureModule> _modules = [];

    public IReadOnlyList<IFeatureModule> Modules =>
        _modules.OrderBy(module => module.Order).ToArray();

    public void Register(IFeatureModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (_modules.Any(existing =>
                string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Il modulo '{module.Id}' è già registrato.");
        }

        _modules.Add(module);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var module in Modules)
        {
            await module.InitializeAsync(cancellationToken);
        }
    }
}
