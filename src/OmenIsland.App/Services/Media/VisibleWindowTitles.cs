using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OmenIsland.App.Services.Media;

internal static class VisibleWindowTitles
{
    public static bool ContainsForProcess(string processName, string fragment)
    {
        HashSet<int> ids;
        try
        {
            ids = Process.GetProcessesByName(processName)
                .Select(process =>
                {
                    var id = process.Id;
                    process.Dispose();
                    return id;
                })
                .ToHashSet();
        }
        catch
        {
            return false;
        }

        if (ids.Count == 0)
        {
            return false;
        }

        var found = false;
        EnumWindows(
            (hwnd, _) =>
            {
                if (found || !IsWindowVisible(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var processId);
                if (!ids.Contains((int)processId))
                {
                    return true;
                }

                var title = new StringBuilder(512);
                if (GetWindowText(hwnd, title, title.Capacity) <= 0)
                {
                    return true;
                }

                if (title.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
