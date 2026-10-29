using OmenIsland.Hardware.HpWmi;
using OmenIsland.Hardware.Models;

namespace OmenIsland.Tests;

public sealed class HpWmiProtocolTests
{
    [Theory]
    [InlineData(PerformanceProfile.Eco, 0x02)]
    [InlineData(PerformanceProfile.Default, 0x00)]
    [InlineData(PerformanceProfile.Quiet, 0x03)]
    [InlineData(PerformanceProfile.Performance, 0x01)]
    public void VictusProfilePayloadUsesLegacyFirmwareValues(
        PerformanceProfile profile,
        byte expectedMode)
    {
        var payload = HpWmiProtocol.PerformancePayload(
            profile,
            thermalPolicyVersion: 0,
            isVictus: true);

        Assert.Equal(new byte[] { 0xFF, expectedMode, 0, 0 }, payload);
    }

    [Theory]
    [InlineData(PerformanceProfile.Eco, 0x50)]
    [InlineData(PerformanceProfile.Default, 0x30)]
    [InlineData(PerformanceProfile.Quiet, 0x03)]
    [InlineData(PerformanceProfile.Performance, 0x31)]
    public void VersionOneOmenPayloadUsesCurrentFirmwareValues(
        PerformanceProfile profile,
        byte expectedMode)
    {
        var payload = HpWmiProtocol.PerformancePayload(
            profile,
            thermalPolicyVersion: 1,
            isVictus: false);

        Assert.Equal(new byte[] { 0xFF, expectedMode, 0, 0 }, payload);
    }

    [Fact]
    public void SystemDesignCapabilitiesAreDecodedFromDocumentedOffsets()
    {
        var data = new byte[128];
        data[3] = 1;
        data[4] = HpWmiProtocol.SoftwareFanControlFlag;

        Assert.Equal(1, HpWmiProtocol.GetThermalPolicyVersion(data));
        Assert.True(HpWmiProtocol.HasSoftwareFanControl(data));
    }

    [Fact]
    public void LegacyFanQueryAddressesOneFanAndDecodesRpm()
    {
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, HpWmiProtocol.LegacyFanQuery(1));
        Assert.Equal(5500, HpWmiProtocol.ReadLegacyRpm([0, 0, 0x15, 0x7C]));
        Assert.Equal(49, HpWmiProtocol.ReadFirmwareCelsius([49, 0, 0, 0]));
    }

    [Theory]
    [InlineData(0x00, PerformanceProfile.Default)]
    [InlineData(0x01, PerformanceProfile.Performance)]
    [InlineData(0x02, PerformanceProfile.Eco)]
    [InlineData(0x03, PerformanceProfile.Quiet)]
    [InlineData(0x31, PerformanceProfile.Performance)]
    public void VictusFirmwareByteMapsToProfile(byte value, PerformanceProfile profile)
    {
        Assert.Equal(profile, HpWmiProtocol.FromFirmwareByte(value, isVictus: true));
    }
}
