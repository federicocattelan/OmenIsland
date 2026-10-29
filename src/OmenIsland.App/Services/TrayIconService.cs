using System.Drawing;
using System.Windows.Threading;
using OmenIsland.App.Presentation;
using OmenIsland.App.Views;
using WinForms = System.Windows.Forms;

namespace OmenIsland.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly IslandWindow _window;
    private readonly IslandViewModel _viewModel;
    private readonly Action _showSettings;
    private readonly Func<Task> _exitAsync;
    private readonly Dispatcher _dispatcher;
    private readonly WinForms.NotifyIcon _notifyIcon;

    public TrayIconService(
        IslandWindow window,
        IslandViewModel viewModel,
        Action showSettings,
        Func<Task> exitAsync)
    {
        _window = window;
        _viewModel = viewModel;
        _showSettings = showSettings;
        _exitAsync = exitAsync;
        _dispatcher = window.Dispatcher;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(
            "Impostazioni",
            null,
            (_, _) => _dispatcher.Invoke(_showSettings));
        menu.Items.Add(
            "Apri controlli",
            null,
            (_, _) => _dispatcher.Invoke(_window.Expand));
        menu.Items.Add(
            "Ventole Auto",
            null,
            async (_, _) => await RestoreAutomaticAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(
            "Esci",
            null,
            async (_, _) => await await _dispatcher.InvokeAsync(_exitAsync));

        var executable = Environment.ProcessPath;
        var icon = string.IsNullOrWhiteSpace(executable)
            ? SystemIcons.Application
            : System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Omen Island",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) =>
            _dispatcher.Invoke(_showSettings);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private async Task RestoreAutomaticAsync()
    {
        await await _dispatcher.InvokeAsync(_viewModel.RestoreAutomaticAsync);
    }
}
