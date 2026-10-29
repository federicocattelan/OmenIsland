using System.Runtime.InteropServices;

namespace OmenIsland.Hardware.HpWmi;

/// <summary>
/// Reads the current OMEN thermal profile from embedded-controller register 0x95.
/// Uses a WinRing0-compatible port driver only when one is already loaded.
/// </summary>
internal static class EcThermalProfileReader
{
    private const ushort CommandPort = 0x66;
    private const ushort DataPort = 0x62;
    private const byte ReadCommand = 0x80;
    private const byte OutputBufferFull = 0x01;
    private const byte InputBufferFull = 0x02;
    private const byte ThermalProfileRegister = 0x95;
    private const int GenericReadWrite = unchecked((int)0xC0000000);

    private static readonly string[] DeviceNames =
    [
        @"\\.\WinRing0_1_2_0"
    ];

    private static readonly uint ReadPortByteCode = ControlCode(40000, 0x833, 0, 1);
    private static readonly uint WritePortByteCode = ControlCode(40000, 0x836, 0, 2);

    private static readonly object Sync = new();
    private static IntPtr _device = IntPtr.Zero;
    private static bool _unavailable;

    public static byte? TryRead()
    {
        lock (Sync)
        {
            if (!EnsureOpen())
            {
                return null;
            }

            try
            {
                return ReadRegister(ThermalProfileRegister);
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool EnsureOpen()
    {
        if (_device != IntPtr.Zero && _device != InvalidHandle)
        {
            return true;
        }

        if (_unavailable)
        {
            return false;
        }

        foreach (var name in DeviceNames)
        {
            var handle = CreateFile(
                name,
                GenericReadWrite,
                3,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero);

            if (handle != IntPtr.Zero && handle != InvalidHandle)
            {
                _device = handle;
                return true;
            }
        }

        _unavailable = true;
        return false;
    }

    private static byte? ReadRegister(byte register)
    {
        if (!WaitUntil(status => (status & InputBufferFull) == 0))
        {
            return null;
        }

        if (!WritePort(CommandPort, ReadCommand))
        {
            return null;
        }

        if (!WaitUntil(status => (status & InputBufferFull) == 0))
        {
            return null;
        }

        if (!WritePort(DataPort, register))
        {
            return null;
        }

        if (!WaitUntil(status => (status & OutputBufferFull) != 0))
        {
            return null;
        }

        return ReadPort(DataPort);
    }

    private static bool WaitUntil(Func<byte, bool> ready)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < 30)
        {
            var status = ReadPort(CommandPort);
            if (status is null)
            {
                return false;
            }

            if (ready(status.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static byte? ReadPort(ushort port)
    {
        var input = (uint)port;
        var output = 0u;
        var returned = 0u;
        var ok = DeviceIoControl(
            _device,
            ReadPortByteCode,
            ref input,
            sizeof(uint),
            ref output,
            sizeof(uint),
            out returned,
            IntPtr.Zero);

        return ok ? (byte)output : null;
    }

    private static bool WritePort(ushort port, byte value)
    {
        var input = new WritePortInput
        {
            Port = port,
            Value = value
        };

        return DeviceIoControl(
            _device,
            WritePortByteCode,
            ref input,
            (uint)Marshal.SizeOf<WritePortInput>(),
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    private static uint ControlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WritePortInput
    {
        public uint Port;
        public byte Value;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        int desiredAccess,
        int shareMode,
        IntPtr securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        ref uint input,
        uint inputSize,
        ref uint output,
        uint outputSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        ref WritePortInput input,
        uint inputSize,
        IntPtr output,
        uint outputSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
