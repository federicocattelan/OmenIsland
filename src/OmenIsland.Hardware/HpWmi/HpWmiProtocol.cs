using OmenIsland.Hardware.Models;

namespace OmenIsland.Hardware.HpWmi;

/// <summary>
/// HP gaming-machine WMI command identifiers. These values are also
/// implemented by the open-source Linux hp-wmi platform driver.
/// </summary>
public static class HpWmiProtocol
{
    public const uint GamingCommand = 0x20008;
    public const uint ReadCommand = 0x01;

    public const byte GetFanCount = 0x10;
    public const byte GetLegacyFanSpeed = 0x11;
    public const byte SetPerformanceMode = 0x1A;
    public const byte GetFirmwareTemperature = 0x23;
    public const byte GetMaxFan = 0x26;
    public const byte SetMaxFan = 0x27;
    public const byte GetSystemDesign = 0x28;
    public const byte GetFanLevel = 0x2D;
    public const byte SetFanSpeed = 0x2E;
    public const byte GetThermalProfile = 0x4C;

    public const byte SoftwareFanControlFlag = 0x01;

    public static byte[] ReadPayload() => [0, 0, 0, 0];

    public static byte[] LegacyFanQuery(byte fanIndex) => [fanIndex, 0, 0, 0];

    public static byte[] FirmwareTemperaturePayload() => [0x01, 0, 0, 0];

    public static int? ReadLegacyRpm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var rpm = (data[2] << 8) | data[3];
        return rpm is >= 0 and < 20000 ? rpm : null;
    }

    public static int? ReadFirmwareCelsius(ReadOnlySpan<byte> data) =>
        data.Length > 0 && data[0] is > 0 and < 125
            ? data[0]
            : null;

    public static byte[] PerformancePayload(
        PerformanceProfile profile,
        byte thermalPolicyVersion,
        bool isVictus)
    {
        // Victus 16-e and thermal-policy 0 use the four legacy firmware modes.
        // Newer OMEN firmware uses 0x30/0x31/0x50, with quiet retained as 0x03.
        var mode = isVictus || thermalPolicyVersion == 0
            ? profile switch
            {
                PerformanceProfile.Eco => (byte)0x02,
                PerformanceProfile.Default => (byte)0x00,
                PerformanceProfile.Quiet => (byte)0x03,
                PerformanceProfile.Performance => (byte)0x01,
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            }
            : profile switch
            {
                PerformanceProfile.Eco => (byte)0x50,
                PerformanceProfile.Default => (byte)0x30,
                PerformanceProfile.Quiet => (byte)0x03,
                PerformanceProfile.Performance => (byte)0x31,
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };

        return [0xFF, mode, 0x00, 0x00];
    }

    public static PerformanceProfile? FromFirmwareByte(byte value, bool isVictus)
    {
        if (isVictus)
        {
            return value switch
            {
                0x00 or 0x30 => PerformanceProfile.Default,
                0x01 or 0x31 => PerformanceProfile.Performance,
                0x02 or 0x50 => PerformanceProfile.Eco,
                0x03 => PerformanceProfile.Quiet,
                _ => null
            };
        }

        return value switch
        {
            0x00 => PerformanceProfile.Performance,
            0x01 or 0x30 => PerformanceProfile.Default,
            0x02 or 0x50 => PerformanceProfile.Eco,
            0x03 => PerformanceProfile.Quiet,
            0x31 => PerformanceProfile.Performance,
            _ => null
        };
    }

    public static byte[] MaxFanPayload(bool enabled) =>
        [enabled ? (byte)1 : (byte)0, 0, 0, 0];

    public static byte[] FanSpeedPayload(byte cpuLevel, byte gpuLevel) =>
        [cpuLevel, gpuLevel, 0, 0];

    public static byte GetThermalPolicyVersion(ReadOnlySpan<byte> systemDesign) =>
        systemDesign.Length > 3 ? systemDesign[3] : (byte)0;

    public static bool HasSoftwareFanControl(ReadOnlySpan<byte> systemDesign) =>
        systemDesign.Length > 4 &&
        (systemDesign[4] & SoftwareFanControlFlag) != 0;
}

public static class FanPercentMapper
{
    public const int MinimumManualPercent = 30;
    public const int MaximumPercent = 100;
    public const byte CpuMaximumLevel = 55;
    public const byte GpuMaximumLevel = 57;
    public const int CpuMaximumRpm = CpuMaximumLevel * 100;
    public const int GpuMaximumRpm = GpuMaximumLevel * 100;

    public static int ClampManualPercent(int percent) =>
        Math.Clamp(percent, MinimumManualPercent, MaximumPercent);

    public static byte ToRawLevel(int percent, byte maximumLevel)
    {
        var safePercent = ClampManualPercent(percent);
        return checked((byte)Math.Round(maximumLevel * safePercent / 100d));
    }

    public static int FromRawLevel(byte level, byte maximumLevel)
    {
        if (maximumLevel == 0)
        {
            return 0;
        }

        return Math.Clamp(
            (int)Math.Round(level * 100d / maximumLevel),
            0,
            MaximumPercent);
    }

    public static int FromRpm(int rpm, int maximumRpm)
    {
        if (maximumRpm <= 0 || rpm <= 0)
        {
            return 0;
        }

        return Math.Clamp(
            (int)Math.Round(rpm * 100d / maximumRpm),
            0,
            MaximumPercent);
    }
}

public sealed record HpWmiResult(int ReturnCode, byte[] Data)
{
    public bool IsSuccess => ReturnCode == 0;
}
