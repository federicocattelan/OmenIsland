using OmenIsland.Hardware.HpWmi;

namespace OmenIsland.Tests;

public sealed class FanPercentMapperTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(55, 55)]
    [InlineData(101, 100)]
    public void ManualPercentIsClamped(int input, int expected)
    {
        Assert.Equal(expected, FanPercentMapper.ClampManualPercent(input));
    }

    [Fact]
    public void MaximumPercentMapsToFirmwareMaximum()
    {
        Assert.Equal(
            FanPercentMapper.CpuMaximumLevel,
            FanPercentMapper.ToRawLevel(
                100,
                FanPercentMapper.CpuMaximumLevel));
        Assert.Equal(
            FanPercentMapper.GpuMaximumLevel,
            FanPercentMapper.ToRawLevel(
                100,
                FanPercentMapper.GpuMaximumLevel));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(55, 100)]
    [InlineData(28, 51)]
    public void RawCpuLevelConvertsToDisplayPercent(
        byte raw,
        int expected)
    {
        Assert.Equal(
            expected,
            FanPercentMapper.FromRawLevel(
                raw,
                FanPercentMapper.CpuMaximumLevel));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2750, 50)]
    [InlineData(5500, 100)]
    public void LegacyRpmConvertsToDisplayPercent(int rpm, int expected)
    {
        Assert.Equal(
            expected,
            FanPercentMapper.FromRpm(rpm, FanPercentMapper.CpuMaximumRpm));
    }
}
