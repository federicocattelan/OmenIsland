using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;

namespace OmenIsland.App.Views;

/// <summary>
/// Tray menu styled like the settings window. Opens above the cursor, closes on click outside or Esc.
/// </summary>
public partial class TrayMenuWindow : Window
{
    private readonly Action _restartIsland;
    private readonly Action _openSettings;
    private readonly Func<Task> _restoreFans;
    private readonly Func<Task> _exit;
    private bool _closing;

    public TrayMenuWindow(
        string subtitle,
        Action restartIsland,
        Action openSettings,
        Func<Task> restoreFans,
        Func<Task> exit)
    {
        InitializeComponent();
        Subtitle.Text = subtitle;
        _restartIsland = restartIsland;
        _openSettings = openSettings;
        _restoreFans = restoreFans;
        _exit = exit;
    }

    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenIsland");

    /// <summary>Shows the menu with its bottom-right corner at the cursor, kept inside the work area.</summary>
    public void ShowAtCursor()
    {
        var cursor = WinForms.Cursor.Position;
        var workArea = WinForms.Screen.FromPoint(cursor).WorkingArea;

        // Show first so the window has a DPI and a measured size, then place it.
        Left = -10000;
        Top = -10000;
        Show();
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? System.Windows.Media.Matrix.Identity;
        var cursorDip = toDip.Transform(new Point(cursor.X, cursor.Y));
        var topLeft = toDip.Transform(new Point(workArea.Left, workArea.Top));
        var bottomRight = toDip.Transform(new Point(workArea.Right, workArea.Bottom));

        // The card has a 12 DIP shadow margin; let that margin overhang the cursor.
        Left = Math.Clamp(cursorDip.X - ActualWidth + 12, topLeft.X - 12, bottomRight.X - ActualWidth + 12);
        Top = Math.Clamp(cursorDip.Y - ActualHeight + 12, topLeft.Y - 12, bottomRight.Y - ActualHeight + 12);

        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Focus();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        CardOffset.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    private void Run(Action action)
    {
        Dismiss();
        action();
    }

    private async void RunAsync(Func<Task> action)
    {
        Dismiss();
        try
        {
            await action();
        }
        catch
        {
            // Tray actions report their own errors; the menu must not crash the app.
        }
    }

    private void RestartIsland_Click(object sender, RoutedEventArgs e) => Run(_restartIsland);

    private void Settings_Click(object sender, RoutedEventArgs e) => Run(_openSettings);

    private void FansAuto_Click(object sender, RoutedEventArgs e) => RunAsync(_restoreFans);

    private void Exit_Click(object sender, RoutedEventArgs e) => RunAsync(_exit);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogFolder}\"") { UseShellExecute = true });
        }
        catch
        {
            // Opening the folder is a convenience only.
        }
    });

    private void Window_Deactivated(object? sender, EventArgs e) => Dismiss();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
