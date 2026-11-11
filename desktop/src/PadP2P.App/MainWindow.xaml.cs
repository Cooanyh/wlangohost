using System.ComponentModel;
using System.Windows;
using PadP2P.App.Services;
using PadP2P.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class MainWindow : FluentWindow
{
    public bool AllowApplicationExit { get; set; }

    internal MainWindow(AppViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyIconTheme(IconThemeService.IsDark);
        SystemThemeWatcher.Watch(this);
    }

    internal void ApplyIconTheme(bool isDark)
    {
        IconThemeService.ApplyTo(this, isDark);
        var icon = IconThemeService.LoadTitleIcon(isDark);
        AppTitleBar.Icon = new ImageIcon { Source = icon };
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowApplicationExit) return;
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
    }
}
