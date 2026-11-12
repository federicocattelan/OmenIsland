using OmenIsland.App.Services.Headphones;

namespace OmenIsland.Tests;

public sealed class NothingProtocolTests
{
    [Fact]
    public void EncodeBuildsHeaderAndCrc()
    {
        var frame = NothingProtocol.Encode(NothingProtocol.BatteryGet, [], 1);

        Assert.Equal(
            new byte[] { 0x55, 0x60, 0x01, 0x07, 0xC0, 0x00, 0x00, 0x01, 0xAC, 0xDF },
            frame);
    }

    [Fact]
    public void EncodeIncludesPayloadLength()
    {
        var frame = NothingProtocol.Encode(
            NothingProtocol.NoiseSet,
            NothingProtocol.NoiseSetPayload(NoiseMode.AncAdaptive),
            9);

        Assert.Equal(0x0F, frame[3]);
        Assert.Equal(0xF0, frame[4]);
        Assert.Equal(3, frame[5]);
        Assert.Equal(new byte[] { 0x01, 0x04, 0x00 }, frame[8..11]);
        Assert.Equal(13, frame.Length);
    }

    [Fact]
    public void ParsesSingleBatteryWithChargingBit()
    {
        var reading = NothingProtocol.ParseBattery([0x01, 0x06, 0xD2]);

        Assert.Equal(new BatteryReading(82, true), reading);
    }

    [Fact]
    public void PrefersSingleBatteryOverOtherEntries()
    {
        var reading = NothingProtocol.ParseBattery([0x02, 0x04, 0x50, 0x06, 0x2A]);

        Assert.Equal(new BatteryReading(42, false), reading);
    }

    [Fact]
    public void ReaderReassemblesSplitAndJoinedFrames()
    {
        var battery = Reply(NothingProtocol.BatteryNotify, [0x01, 0x06, 0x40], 3);
        var noise = Reply(NothingProtocol.NoiseNotify, [0x01, 0x07], 4);
        var stream = new byte[] { 0x00, 0x13 }.Concat(battery).Concat(noise).ToArray();
        var reader = new NothingFrameReader();
        var frames = new List<NothingFrame>();

        reader.Append(stream.AsSpan(0, 7));
        while (reader.TryRead(out var frame))
        {
            frames.Add(frame);
        }

        Assert.Empty(frames);

        reader.Append(stream.AsSpan(7));
        while (reader.TryRead(out var frame))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(NothingProtocol.BatteryNotify, frames[0].Command);
        Assert.Equal(3, frames[0].Sequence);
        Assert.Equal(new BatteryReading(64, false), NothingProtocol.ParseBattery(frames[0].Payload));
        Assert.Equal(NoiseMode.Transparency, NothingProtocol.ParseNoise(frames[1].Payload));
    }

    [Theory]
    [InlineData(NoiseMode.Off, 0x05)]
    [InlineData(NoiseMode.Transparency, 0x07)]
    [InlineData(NoiseMode.AncHigh, 0x01)]
    [InlineData(NoiseMode.AncMid, 0x02)]
    [InlineData(NoiseMode.AncLow, 0x03)]
    [InlineData(NoiseMode.AncAdaptive, 0x04)]
    public void NoiseModesRoundTrip(NoiseMode mode, byte value)
    {
        Assert.Equal(value, NothingProtocol.NoiseModeToByte(mode));
        Assert.Equal(mode, NothingProtocol.NoiseModeFromByte(value));
    }

    [Fact]
    public void SpatialAndCodecMapToWireValues()
    {
        Assert.Equal(new byte[] { 0x02, 0x00 }, NothingProtocol.SpatialSetPayload(SpatialMode.Concert));
        Assert.Equal(new byte[] { 0x03, 0x00 }, NothingProtocol.SpatialSetPayload(SpatialMode.Theatre));
        Assert.Equal(SpatialMode.Theatre, NothingProtocol.ParseSpatial([0x03, 0x00]));
        Assert.Null(NothingProtocol.ParseSpatial([0x01, 0x01]));
        Assert.Equal(new byte[] { 0x02 }, NothingProtocol.CodecSetPayload(AudioCodec.Ldac));
        Assert.Equal(AudioCodec.Aac, NothingProtocol.ParseCodec([0x00]));
    }

    [Fact]
    public void LowLatencyMapsToWireValues()
    {
        Assert.Equal(new byte[] { 0x01, 0x00 }, NothingProtocol.LowLatencySetPayload(true));
        Assert.Equal(new byte[] { 0x02, 0x00 }, NothingProtocol.LowLatencySetPayload(false));
        Assert.True(NothingProtocol.ParseLowLatency([0x01]));
        Assert.False(NothingProtocol.ParseLowLatency([0x02]));
        Assert.Null(NothingProtocol.ParseLowLatency([]));
    }

    [Theory]
    [InlineData(EqPreset.Rock, 0x01)]
    [InlineData(EqPreset.Electronic, 0x02)]
    [InlineData(EqPreset.Pop, 0x03)]
    [InlineData(EqPreset.Vocals, 0x04)]
    [InlineData(EqPreset.Classical, 0x05)]
    [InlineData(EqPreset.Custom, 0x06)]
    public void ListeningModesMapToB175Bytes(EqPreset preset, byte value)
    {
        Assert.Equal(new byte[] { value, 0x00 }, NothingProtocol.EqSetPayload(preset));
        Assert.Equal(preset, NothingProtocol.ParseEq([value, 0x00]));
    }

    [Fact]
    public void UnknownListeningModeIsIgnored()
    {
        Assert.Null(NothingProtocol.ParseEq([0x07, 0x00]));
    }

    [Fact]
    public void CustomEqPayloadIsPeakHighShelfLowShelf()
    {
        var payload = NothingProtocol.CustomEqSetPayload(new CustomEq(Bass: 3, Mid: 0, Treble: -2));

        Assert.Equal(1 + 4 + 3 * 13 + 3 * 3, payload.Length);
        Assert.Equal(3, payload[0]);
        // Total gain is minus the loudest band: -3.0f, little-endian.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x40, 0xC0 }, payload[1..5]);
        // Mid band: peak, 0 dB, 980 Hz.
        Assert.Equal(1, payload[5]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, payload[6..10]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x75, 0x44 }, payload[10..14]);
        Assert.Equal(2, payload[18]);
        Assert.Equal(0, payload[31]);
        Assert.All(payload[^9..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void CustomEqRoundTripsAndClamps()
    {
        var payload = NothingProtocol.CustomEqSetPayload(new CustomEq(Bass: 9, Mid: -4, Treble: 1));

        Assert.Equal(new CustomEq(Bass: 6, Mid: -4, Treble: 1), NothingProtocol.ParseCustomEq(payload));
        Assert.Null(NothingProtocol.ParseCustomEq(payload[..20]));
    }

    // Device replies set the CRC flag (0x20) in the control word.
    private static byte[] Reply(ushort command, byte[] payload, byte sequence)
    {
        var frame = NothingProtocol.Encode(command, payload, sequence);
        frame[1] |= 0x20;
        return frame;
    }
}
