using System.ComponentModel;
using System.Windows;

namespace OmenIsland.App.Views;

public partial class SettingsWindow : Window
{
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public void PermitClose() => _allowClose = true;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
