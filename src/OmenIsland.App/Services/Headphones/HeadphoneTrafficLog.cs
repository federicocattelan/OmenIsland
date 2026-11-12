using System.IO;

namespace OmenIsland.App.Services.Headphones;

/// <summary>
/// Writes every control frame except battery polls to %LOCALAPPDATA%\OmenIsland\headphones.log,
/// so a setting the headphones ignore or reset shows up as missing or different replies.
/// </summary>
internal static class HeadphoneTrafficLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenIsland",
        "headphones.log");

    public static void Frame(string direction, ushort command, byte sequence, ReadOnlySpan<byte> payload)
    {
        if (command is NothingProtocol.BatteryGet
            or NothingProtocol.BatteryReturn
            or NothingProtocol.BatteryNotify)
        {
            return;
        }

        Write($"{direction} cmd=0x{command:X4} seq={sequence} payload=[{Convert.ToHexString(payload)}]");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break the headphone connection.
        }
    }
}
