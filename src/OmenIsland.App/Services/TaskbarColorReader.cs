using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace OmenIsland.App.Services;

public static class TaskbarColorReader
{
    public static Color ReadWallpaperAccent()
    {
        try
        {
            var accent = ReadRegistryDword(@"Software\Microsoft\Windows\DWM", "AccentColor")
                ?? ReadRegistryDword(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentColorMenu");
            if (accent is uint stored)
            {
                return FromAbgr(stored);
            }

            if (DwmGetColorizationColor(out var color, out _) != 0)
            {
                return Color.FromRgb(226, 75, 75);
            }

            return Color.FromRgb(
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF));
        }
        catch
        {
            return Color.FromRgb(226, 75, 75);
        }
    }

    private static uint? ReadRegistryDword(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) switch
        {
            int signed => unchecked((uint)signed),
            uint unsigned => unsigned,
            _ => null
        };
    }

    private static Color FromAbgr(uint value) =>
        Color.FromRgb(
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF));

    public static Color Read()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var accentOnTaskbar = (int)(personalize?.GetValue("ColorPrevalence") ?? 0) == 1;
            var lightTaskbar = (int)(personalize?.GetValue("SystemUsesLightTheme") ?? 0) == 1;

            if (!accentOnTaskbar ||
                DwmGetColorizationColor(out var color, out _) != 0)
            {
                return lightTaskbar
                    ? Color.FromRgb(243, 243, 243)
                    : Colors.Black;
            }

            return Color.FromRgb(
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF));
        }
        catch
        {
            return Colors.Black;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(
        out uint pcrColorization,
        out bool pfOpaqueBlend);
}
