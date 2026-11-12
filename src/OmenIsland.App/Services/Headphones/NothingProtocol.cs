namespace OmenIsland.App.Services.Headphones;

public enum NoiseMode
{
    Off,
    Transparency,
    AncHigh,
    AncMid,
    AncLow,
    AncAdaptive
}

/// <summary>B175 listening modes (ear-web "forretress" lineup).</summary>
public enum EqPreset
{
    DiracOpteo,
    Rock,
    Electronic,
    Pop,
    Vocals,
    Classical,
    Custom
}

public enum SpatialMode
{
    Off,
    Concert,
    Theatre
}

public enum AudioCodec
{
    Aac,
    Ldac
}

public readonly record struct BatteryReading(int Percent, bool IsCharging);

/// <summary>Custom EQ gains in whole dB, each in [-6, 6].</summary>
public readonly record struct CustomEq(int Bass, int Mid, int Treble);

public readonly record struct NothingFrame(ushort Command, byte Sequence, byte[] Payload);

/// <summary>
/// Nothing/CMF control protocol over RFCOMM, as used by the CMF Headphone Pro (B175).
/// Frame: 55 60 01 | cmd (LE) | len (LE) | seq | payload | CRC-16/MODBUS (LE).
/// </summary>
public static class NothingProtocol
{
    public static readonly Guid ServiceUuid = new("aeac4a03-dff5-498f-843a-34487cf133eb");

    public const ushort ProtocolGet = 0xC001;
    public const ushort ProtocolSet = 0xF001;

    public const ushort BatteryGet = 0xC007;
    public const ushort BatteryNotify = 0xE001;
    public const ushort BatteryReturn = 0x4007;

    public const ushort NoiseGet = 0xC01E;
    public const ushort NoiseSet = 0xF00F;
    public const ushort NoiseNotify = 0xE003;
    public const ushort NoiseNotifyAlt = 0x6003;
    public const ushort NoiseReturn = 0x401E;

    public const ushort EqGet = 0xC050;
    public const ushort EqSet = 0xF01D;
    public const ushort EqReturn = 0x4050;

    public const ushort CustomEqGet = 0xC044;
    public const ushort CustomEqSet = 0xF041;
    public const ushort CustomEqReturn = 0x4044;

    public const int CustomEqMaxGain = 6;

    public const ushort DualConnectionGet = 0xC027;
    public const ushort DualConnectionSet = 0xF01A;
    public const ushort DualConnectionReturn = 0x4027;

    public const ushort SpatialGet = 0xC04F;
    public const ushort SpatialSet = 0xF052;
    public const ushort SpatialReturn = 0x404F;

    public const ushort CodecGet = 0xC029;
    public const ushort CodecSet = 0xF01C;
    public const ushort CodecReturn = 0x4029;

    public const ushort LowLatencyGet = 0xC041;
    public const ushort LowLatencySet = 0xF040;
    public const ushort LowLatencyReturn = 0x4041;

    public const int HeaderLength = 8;
    private const int CrcLength = 2;
    private const byte SingleBatteryId = 0x06;

    public static byte[] Encode(ushort command, ReadOnlySpan<byte> payload, byte sequence)
    {
        var frame = new byte[HeaderLength + payload.Length + CrcLength];
        frame[0] = 0x55;
        frame[1] = 0x60;
        frame[2] = 0x01;
        frame[3] = (byte)(command & 0xFF);
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)payload.Length;
        frame[6] = 0x00;
        frame[7] = sequence;
        payload.CopyTo(frame.AsSpan(HeaderLength));

        var crc = Crc16(frame.AsSpan(0, HeaderLength + payload.Length));
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    public static BatteryReading? ParseBattery(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            return null;
        }

        var count = payload[0];
        BatteryReading? first = null;
        for (var i = 0; i < count; i++)
        {
            var index = 1 + i * 2;
            if (index + 1 >= payload.Length)
            {
                break;
            }

            var reading = new BatteryReading(
                Math.Clamp(payload[index + 1] & 0x7F, 0, 100),
                (payload[index + 1] & 0x80) != 0);
            if (payload[index] == SingleBatteryId)
            {
                return reading;
            }

            first ??= reading;
        }

        return first;
    }

    public static byte NoiseModeToByte(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => 0x05,
        NoiseMode.Transparency => 0x07,
        NoiseMode.AncHigh => 0x01,
        NoiseMode.AncMid => 0x02,
        NoiseMode.AncLow => 0x03,
        NoiseMode.AncAdaptive => 0x04,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static NoiseMode? NoiseModeFromByte(byte value) => value switch
    {
        0x05 => NoiseMode.Off,
        0x07 => NoiseMode.Transparency,
        0x01 => NoiseMode.AncHigh,
        0x02 => NoiseMode.AncMid,
        0x03 => NoiseMode.AncLow,
        0x04 => NoiseMode.AncAdaptive,
        _ => null
    };

    public static byte[] NoiseSetPayload(NoiseMode mode) => [0x01, NoiseModeToByte(mode), 0x00];

    public static NoiseMode? ParseNoise(ReadOnlySpan<byte> payload) =>
        payload.Length >= 2 ? NoiseModeFromByte(payload[1]) : null;

    public static EqPreset? EqFromByte(byte value) =>
        value <= (byte)EqPreset.Custom ? (EqPreset)value : null;

    public static byte[] EqSetPayload(EqPreset preset) => [(byte)preset, 0x00];

    public static EqPreset? ParseEq(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1 ? EqFromByte(payload[0]) : null;

    // Custom EQ is three biquads: peak (mid), high shelf (treble), low shelf (bass), in that order.
    private const byte LowShelf = 0;
    private const byte Peak = 1;
    private const byte HighShelf = 2;
    private const int CustomEqBandLength = 13;

    /// <summary>
    /// Payload: band count | total gain f32 | per band: type, gain f32, freq f32, Q f32 | 3 zero bytes per band.
    /// Floats are little-endian. Total gain is minus the highest positive band gain, so the device never clips.
    /// </summary>
    public static byte[] CustomEqSetPayload(CustomEq eq)
    {
        var bass = ClampGain(eq.Bass);
        var mid = ClampGain(eq.Mid);
        var treble = ClampGain(eq.Treble);
        (byte Type, float Gain, float Frequency, float Q)[] bands =
        [
            (Peak, mid, 980f, 0.7f),
            (HighShelf, treble, 3500f, 1.0f),
            (LowShelf, bass, 140f, 0.8f)
        ];

        var payload = new byte[1 + 4 + bands.Length * CustomEqBandLength + bands.Length * 3];
        payload[0] = (byte)bands.Length;
        WriteSingle(payload, 1, -Math.Max(0, Math.Max(bass, Math.Max(mid, treble))));
        var offset = 5;
        foreach (var band in bands)
        {
            payload[offset] = band.Type;
            WriteSingle(payload, offset + 1, band.Gain);
            WriteSingle(payload, offset + 5, band.Frequency);
            WriteSingle(payload, offset + 9, band.Q);
            offset += CustomEqBandLength;
        }

        return payload;
    }

    public static CustomEq? ParseCustomEq(ReadOnlySpan<byte> payload)
    {
        // Gains sit after the count byte, the total gain and each band's type byte.
        const int firstGain = 6;
        if (payload.Length < firstGain + 2 * CustomEqBandLength + 4)
        {
            return null;
        }

        var gains = new int[3];
        for (var band = 0; band < gains.Length; band++)
        {
            var gain = BitConverter.ToSingle(payload.Slice(firstGain + band * CustomEqBandLength, 4));
            gains[band] = float.IsFinite(gain) ? ClampGain((int)MathF.Round(gain)) : 0;
        }

        return new CustomEq(Bass: gains[2], Mid: gains[0], Treble: gains[1]);
    }

    private static int ClampGain(int gain) => Math.Clamp(gain, -CustomEqMaxGain, CustomEqMaxGain);

    private static void WriteSingle(byte[] buffer, int offset, float value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    public static bool? ParseFlag(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1 ? payload[0] == 0x01 : null;

    public static byte[] SpatialSetPayload(SpatialMode mode) => mode switch
    {
        SpatialMode.Off => [0x00, 0x00],
        SpatialMode.Concert => [0x02, 0x00],
        SpatialMode.Theatre => [0x03, 0x00],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static SpatialMode? ParseSpatial(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            return null;
        }

        return payload[0] switch
        {
            0x00 => SpatialMode.Off,
            0x02 => SpatialMode.Concert,
            0x03 => SpatialMode.Theatre,
            _ => null
        };
    }

    public static byte[] CodecSetPayload(AudioCodec codec) =>
        [codec == AudioCodec.Ldac ? (byte)0x02 : (byte)0x00];

    public static AudioCodec? ParseCodec(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1
            ? payload[0] == 0x02 ? AudioCodec.Ldac : AudioCodec.Aac
            : null;

    /// <summary>Low-latency (gaming) mode: 0x01 on, 0x02 off.</summary>
    public static byte[] LowLatencySetPayload(bool enabled) =>
        [enabled ? (byte)0x01 : (byte)0x02, 0x00];

    public static bool? ParseLowLatency(ReadOnlySpan<byte> payload) =>
        payload.Length >= 1 ? payload[0] == 0x01 : null;
}

/// <summary>Reassembles frames from a byte stream that may split or join them.</summary>
public sealed class NothingFrameReader
{
    private const int MaxBuffered = 4096;
    private readonly List<byte> _buffer = [];

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            _buffer.Add(value);
        }

        if (_buffer.Count > MaxBuffered)
        {
            _buffer.Clear();
        }
    }

    public bool TryRead(out NothingFrame frame)
    {
        frame = default;
        var start = _buffer.IndexOf(0x55);
        if (start < 0)
        {
            _buffer.Clear();
            return false;
        }

        if (start > 0)
        {
            _buffer.RemoveRange(0, start);
        }

        if (_buffer.Count < NothingProtocol.HeaderLength)
        {
            return false;
        }

        var control = _buffer[1] | (_buffer[2] << 8);
        var hasCrc = (control & 0x20) != 0;
        var payloadLength = _buffer[5] | (_buffer[6] << 8);
        var total = NothingProtocol.HeaderLength + payloadLength + (hasCrc ? 2 : 0);
        if (_buffer.Count < total)
        {
            return false;
        }

        var command = (ushort)(_buffer[3] | (_buffer[4] << 8));
        var sequence = _buffer[7];
        var payload = _buffer.GetRange(NothingProtocol.HeaderLength, payloadLength).ToArray();
        _buffer.RemoveRange(0, total);
        frame = new NothingFrame(command, sequence, payload);
        return true;
    }
}
