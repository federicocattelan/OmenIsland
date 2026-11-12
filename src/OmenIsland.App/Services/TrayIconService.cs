using System.Drawing;
using System.Windows.Threading;
using OmenIsland.App.Presentation;
using OmenIsland.App.Views;
using WinForms = System.Windows.Forms;

namespace OmenIsland.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly IslandViewModel _viewModel;
    private readonly Action _showSettings;
    private readonly Action _restartIsland;
    private readonly Func<Task> _exitAsync;
    private readonly Dispatcher _dispatcher;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private TrayMenuWindow? _menu;

    public TrayIconService(
        IslandViewModel viewModel,
        Dispatcher dispatcher,
        Action showSettings,
        Action restartIsland,
        Func<Task> exitAsync)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _showSettings = showSettings;
        _restartIsland = restartIsland;
        _exitAsync = exitAsync;

        var executable = Environment.ProcessPath;
        var icon = string.IsNullOrWhiteSpace(executable)
            ? SystemIcons.Application
            : System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Omen Island",
            Visible = true
        };
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Right)
            {
                _dispatcher.BeginInvoke(ShowMenu);
            }
        };
        _notifyIcon.DoubleClick += (_, _) =>
            _dispatcher.Invoke(_showSettings);
    }

    public void Dispose()
    {
        _menu?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void ShowMenu()
    {
        _menu?.Close();
        var subtitle = $"{_viewModel.ActiveProfileLabel} · Ventole {_viewModel.FanModeLabel}";
        var menu = new TrayMenuWindow(
            subtitle,
            _restartIsland,
            _showSettings,
            _viewModel.RestoreAutomaticAsync,
            _exitAsync);
        menu.Closed += (_, _) =>
        {
            if (_menu == menu)
            {
                _menu = null;
            }
        };
        _menu = menu;
        menu.ShowAtCursor();
    }
}
