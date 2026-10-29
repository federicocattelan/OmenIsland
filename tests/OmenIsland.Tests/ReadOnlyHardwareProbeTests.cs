using OmenIsland.Hardware.HpWmi;

namespace OmenIsland.Tests;

public sealed class ReadOnlyHardwareProbeTests
{
    [Fact]
    public void ProbeUsesReadCommandsOnly()
    {
        using var client = new HpWmiBiosClient();

        if (!client.IsAdministrator)
        {
            var error = Record.Exception(client.Initialize);
            Assert.NotNull(error);
            return;
        }

        client.Initialize();
        Assert.True(client.IsAvailable);

        var design = client.Execute(
            HpWmiProtocol.GetSystemDesign,
            HpWmiProtocol.ReadPayload(),
            128);
        var fanCount = client.Execute(
            HpWmiProtocol.GetFanCount,
            HpWmiProtocol.ReadPayload(),
            4);
        var legacyFan = client.Execute(
            HpWmiProtocol.GetLegacyFanSpeed,
            HpWmiProtocol.LegacyFanQuery(0),
            4);

        Assert.Equal(128, design.Data.Length);
        Assert.True(fanCount.Data.Length > 0);
        Assert.True(legacyFan.Data.Length > 0);
    }
}
