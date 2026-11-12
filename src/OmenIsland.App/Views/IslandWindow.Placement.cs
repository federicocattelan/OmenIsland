using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using OmenIsland.App.Presentation;
using WinForms = System.Windows.Forms;

namespace OmenIsland.App.Views;

/// <summary>
/// Keeps the idle bar on screen. Display, DPI, work-area, session, resume and explorer restarts
/// re-run the layout. A slow watchdog checks that the idle bar is visible, on the primary
/// monitor's top edge, topmost and not covered; it logs what it found to island.log and repairs it.
/// </summary>
public partial class IslandWindow
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LayoutSettleTime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RepeatLogInterval = TimeSpan.FromMinutes(1);
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const long MaxLogBytes = 256 * 1024;

    private DispatcherTimer? _watchdog;
    private DispatcherTimer? _recoveryDebounce;
    private string _pendingRecoveryReason = "";
    private long _lastLayoutTicks;
    private int _coveredStreak;
    private string _lastLoggedProblem = "";
    private long _lastLoggedTicks;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenIsland",
        "island.log");

    private void StartPlacementWatchdog()
    {
        _recoveryDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _recoveryDebounce.Tick += (_, _) =>
        {
            _recoveryDebounce.Stop();
            Recover(_pendingRecoveryReason, alwaysLog: true);
        };

        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = WatchdogInterval };
        _watchdog.Tick += (_, _) => CheckPlacement();
        _watchdog.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) =>
        {
            _watchdog.Stop();
            _recoveryDebounce.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };

        Log($"start: {DescribePlacement()}");
    }

    /// <summary>Records the current placement, and any problem the watchdog would see, before a restart.</summary>
    public void LogPlacement(string reason)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var problem = handle == IntPtr.Zero ? null : FindIdleProblem(handle, out _);
        Log($"{reason}: {DescribePlacement()} problem={problem ?? "none"}");
    }

    private void HandlePlacementMessage(int msg, IntPtr wParam)
    {
        if (msg == WmDisplayChange)
        {
            ScheduleRecovery("display change");
        }
        else if (msg == WmDpiChanged)
        {
            ScheduleRecovery("DPI change");
        }
        else if (msg == WmSettingChange && wParam == (IntPtr)SpiSetWorkArea)
        {
            ScheduleRecovery("work area change");
        }
        else if (msg == WmTaskbarCreated && WmTaskbarCreated != 0)
        {
            ScheduleRecovery("explorer restart");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        ScheduleRecoveryFromAnyThread("display settings");

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
        {
            ScheduleRecoveryFromAnyThread($"session {e.Reason}");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ScheduleRecoveryFromAnyThread("resume");
        }
    }

    private void ScheduleRecoveryFromAnyThread(string reason) =>
        Dispatcher.BeginInvoke(() => ScheduleRecovery(reason));

    // Display changes arrive in bursts; wait until they settle before laying out again.
    private void ScheduleRecovery(string reason)
    {
        if (_recoveryDebounce is null)
        {
            return;
        }

        _pendingRecoveryReason = _recoveryDebounce.IsEnabled
            ? $"{_pendingRecoveryReason}, {reason}"
            : reason;
        _recoveryDebounce.Stop();
        _recoveryDebounce.Start();
    }

    private void MarkLayoutChanged() => _lastLayoutTicks = Environment.TickCount64;

    private void CheckPlacement()
    {
        if (_stateController.State != IslandState.Idle ||
            Environment.TickCount64 - _lastLayoutTicks < LayoutSettleTime.TotalMilliseconds)
        {
            _coveredStreak = 0;
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var problem = FindIdleProblem(handle, out var covered);
        if (problem is null)
        {
            _coveredStreak = 0;
            return;
        }

        // Tooltips and transient popups can sit over the bar for a moment; act only if it lasts.
        if (covered && ++_coveredStreak < 2)
        {
            return;
        }

        _coveredStreak = 0;
        Recover(problem, alwaysLog: false);
    }

    private string? FindIdleProblem(IntPtr handle, out bool covered)
    {
        covered = false;
        if (!IsWindowVisible(handle))
        {
            return "window not visible";
        }

        if (IsCloaked(handle))
        {
            return "window cloaked by DWM";
        }

        if (!GetWindowRect(handle, out var native))
        {
            return null;
        }

        var window = ToPixelRect(native);
        var screen = PrimaryScreenBounds();
        if (IdlePlacement.FindProblem(window, screen) is { } placement)
        {
            return placement;
        }

        if ((GetWindowLong(handle, GwlExStyle) & WsExTopmost) == 0)
        {
            return "lost topmost";
        }

        // The idle line is opaque, so a hit test on it lands on this window unless something
        // covers it (or the layered surface stopped painting).
        var probe = new PointNative { X = window.Left + window.Width / 2, Y = window.Top + 1 };
        var hit = GetAncestor(WindowFromPoint(probe), GaRoot);
        if (hit == handle || hit == IntPtr.Zero || IsOwnProcess(hit) || IsForegroundFullscreen(screen))
        {
            return null;
        }

        covered = true;
        return $"idle line not hit-testable, top window there: {DescribeWindow(hit)}";
    }

    private void Recover(string reason, bool alwaysLog)
    {
        var before = DescribePlacement();
        if (!IsVisible)
        {
            Show();
        }

        ApplyState(_stateController.State, false);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(
                handle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }

        // Make the layered surface push a fresh frame in case it stopped painting.
        RootHost.InvalidateVisual();
        InvalidateVisual();

        var now = Environment.TickCount64;
        if (alwaysLog ||
            reason != _lastLoggedProblem ||
            now - _lastLoggedTicks > RepeatLogInterval.TotalMilliseconds)
        {
            _lastLoggedProblem = reason;
            _lastLoggedTicks = now;
            Log($"recover ({reason}): {before} -> {DescribePlacement()}");
        }
    }

    private string DescribePlacement()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var builder = new StringBuilder();
        builder.Append($"state={_stateController.State} wpfVisible={IsVisible}");
        if (handle != IntPtr.Zero)
        {
            GetWindowRect(handle, out var native);
            var exStyle = GetWindowLong(handle, GwlExStyle);
            builder.Append($" visible={IsWindowVisible(handle)} cloaked={IsCloaked(handle)}");
            builder.Append($" topmost={(exStyle & WsExTopmost) != 0} rect={ToPixelRect(native)}");
            builder.Append($" dpi={GetDpiForWindow(handle)}");
        }

        builder.Append(FormattableString.Invariant($" dip=({Left:0.#},{Top:0.#} {Width:0.#}x{Height:0.#})"));
        builder.Append($" primary={PrimaryScreenBounds()}");
        builder.Append($" screens={WinForms.Screen.AllScreens.Length}");
        return builder.ToString();
    }

    private static PixelRect PrimaryScreenBounds()
    {
        var bounds = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).Bounds;
        return new PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static PixelRect ToPixelRect(RectNative rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static bool IsCloaked(IntPtr handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsOwnProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId == Environment.ProcessId;
    }

    private static bool IsForegroundFullscreen(PixelRect screen)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero ||
            foreground == GetShellWindow() ||
            ClassName(foreground) is "WorkerW" or "Progman" ||
            !GetWindowRect(foreground, out var native))
        {
            return false;
        }

        var rect = ToPixelRect(native);
        return rect.Left <= screen.Left && rect.Top <= screen.Top &&
               rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    private static string DescribeWindow(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        string process;
        try
        {
            using var owner = Process.GetProcessById((int)processId);
            process = owner.ProcessName;
        }
        catch
        {
            process = "?";
        }

        var title = new StringBuilder(128);
        GetWindowText(handle, title, title.Capacity);
        GetWindowRect(handle, out var native);
        var topmost = (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;
        return $"{process}({processId}) class={ClassName(handle)} title=\"{title}\" " +
               $"rect={ToPixelRect(native)} topmost={topmost}";
    }

    private static string ClassName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static void Log(string message)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxLogBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never take the island down.
        }
    }

    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int SpiSetWorkArea = 0x002F;
    private const int WsExTopmost = 0x00000008;
    private const int DwmwaCloaked = 14;
    private const uint GaRoot = 2;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(PointNative point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
