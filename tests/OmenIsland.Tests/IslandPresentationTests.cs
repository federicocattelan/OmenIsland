using OmenIsland.App.Presentation;

namespace OmenIsland.Tests;

public sealed class IslandPresentationTests
{
    [Fact]
    public void StateControllerImplementsThreeStateInteraction()
    {
        var controller = new IslandStateController();

        Assert.Equal(IslandState.Idle, controller.State);

        controller.PointerEntered();
        Assert.Equal(IslandState.Compact, controller.State);

        controller.Activate();
        Assert.Equal(IslandState.Expanded, controller.State);

        controller.Collapse();
        Assert.Equal(IslandState.Idle, controller.State);
    }

    [Fact]
    public void ExpandedPanelFitsThreeColumnsOnFullHd()
    {
        var dimensions = IslandLayoutCalculator.Calculate(
            IslandState.Expanded,
            1920,
            1080);

        Assert.Equal(614, dimensions.Width);
        Assert.Equal(420, dimensions.Height);

        var withPlayer = IslandLayoutCalculator.Calculate(
            IslandState.Expanded,
            1920,
            1080,
            showPlayerColumn: true);
        Assert.Equal(850, withPlayer.Width);
    }

    [Fact]
    public void HeadphonesColumnWidensExpandedPanel()
    {
        var withBoth = IslandLayoutCalculator.Calculate(
            IslandState.Expanded,
            1920,
            1080,
            showPlayerColumn: true,
            showHeadphonesColumn: true);
        var headphonesOnly = IslandLayoutCalculator.Calculate(
            IslandState.Expanded,
            1920,
            1080,
            showHeadphonesColumn: true);
        var narrowScreen = IslandLayoutCalculator.Calculate(
            IslandState.Expanded,
            1024,
            768,
            showPlayerColumn: true,
            showHeadphonesColumn: true);

        Assert.Equal(1062, withBoth.Width);
        Assert.Equal(826, headphonesOnly.Width);
        Assert.Equal(1000, narrowScreen.Width);
    }

    [Fact]
    public void CompactBarUsesContentWidth()
    {
        var narrow = IslandLayoutCalculator.Calculate(
            IslandState.Compact,
            1920,
            1080,
            compactContentWidth: 480);
        var wide = IslandLayoutCalculator.Calculate(
            IslandState.Compact,
            1920,
            1080,
            compactContentWidth: 4000);

        Assert.Equal(480, narrow.Width);
        Assert.Equal(88, narrow.Height);
        Assert.Equal(1896, wide.Width);
    }

    [Fact]
    public void IdleLineApproximatesFourCentimetersOnFullHd()
    {
        var dimensions = IslandLayoutCalculator.Calculate(
            IslandState.Idle,
            1920,
            1080);

        Assert.InRange(dimensions.Width, 216, 218);
        Assert.Equal(7, dimensions.Height);
    }
}
