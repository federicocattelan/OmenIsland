using OmenIsland.App.Presentation;

namespace OmenIsland.Tests;

public sealed class IdlePlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 2560, 1440);

    [Fact]
    public void BarOnTopEdgeIsFine() =>
        Assert.Null(IdlePlacement.FindProblem(new PixelRect(1135, 0, 1424, 7), Screen));

    [Theory]
    [InlineData(1135, 40, 1424, 47)]
    [InlineData(1135, -7, 1424, 0)]
    [InlineData(2600, 0, 2889, 7)]
    [InlineData(-300, 0, -11, 7)]
    [InlineData(1135, 0, 1424, 110)]
    [InlineData(1135, 0, 1135, 7)]
    public void MisplacedBarIsReported(int left, int top, int right, int bottom) =>
        Assert.NotNull(IdlePlacement.FindProblem(new PixelRect(left, top, right, bottom), Screen));
}
