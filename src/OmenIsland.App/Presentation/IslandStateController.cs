namespace OmenIsland.App.Presentation;

public enum IslandState
{
    Idle,
    Compact,
    Expanded
}

public sealed class IslandStateController
{
    public IslandState State { get; private set; } = IslandState.Idle;

    public event EventHandler<IslandState>? StateChanged;

    public void PointerEntered()
    {
        if (State == IslandState.Idle)
        {
            SetState(IslandState.Compact);
        }
    }

    public void PointerLeft()
    {
        if (State == IslandState.Compact)
        {
            SetState(IslandState.Idle);
        }
    }

    public void Activate()
    {
        if (State != IslandState.Expanded)
        {
            SetState(IslandState.Expanded);
        }
    }

    public void Collapse() => SetState(IslandState.Idle);

    public void ShowCompact() => SetState(IslandState.Compact);

    private void SetState(IslandState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}

public readonly record struct IslandDimensions(double Width, double Height);

public static class IslandLayoutCalculator
{
    private const double IdleWidthRatio = 0.113;

    public static IslandDimensions Calculate(
        IslandState state,
        double workAreaWidth,
        double workAreaHeight,
        double compactContentWidth = 0,
        bool showPlayerColumn = false)
    {
        var maxWidth = Math.Max(160, workAreaWidth - 24);
        return state switch
        {
            IslandState.Idle => new IslandDimensions(
                Math.Clamp(workAreaWidth * IdleWidthRatio, 160, 360),
                7),
            IslandState.Compact => new IslandDimensions(
                Math.Clamp(
                    compactContentWidth > 0 ? compactContentWidth : 420,
                    200,
                    maxWidth),
                88),
            IslandState.Expanded => new IslandDimensions(
                Math.Min(showPlayerColumn ? 850 : 614, maxWidth),
                Math.Min(420, Math.Max(360, workAreaHeight - 24))),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }
}
