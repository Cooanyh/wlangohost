using System.Diagnostics;
using System.Windows;
using PadP2P.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class UpdateNoticeWindow : FluentWindow
{
    private const string ReleasesUrl = "https://github.com/Cooanyh/wlangohost/releases/latest";
    private readonly UpdateRelease _release;

    internal UpdateNoticeWindow(UpdateRelease release)
    {
        _release = release;
        InitializeComponent();
        RefreshMessage();
        UiText.LanguageChanged += UiText_LanguageChanged;
        Closed += (_, _) => UiText.LanguageChanged -= UiText_LanguageChanged;
        IconThemeService.ApplyTo(this);
        SystemThemeWatcher.Watch(this);
    }

    public bool IgnoreThisVersion { get; private set; }

    private void UiText_LanguageChanged(object? sender, EventArgs e) => RefreshMessage();

    private void RefreshMessage()
    {
        MessageText.Text = UiText.Format(
            "UpdateDialog.Message",
            _release.Tag,
            UpdateCheckService.CurrentVersion.ToString(3));
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
            DialogResult = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = UiText.Get("Update.OpenFailed");
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        IgnoreThisVersion = true;
        DialogResult = false;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
