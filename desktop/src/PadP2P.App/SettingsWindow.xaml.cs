using System.Diagnostics;
using System.Windows;
using PadP2P.App.Services;
using PadP2P.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class SettingsWindow : FluentWindow
{
    private const string ReleasesUrl = "https://github.com/Cooanyh/wlangohost/releases/latest";
    private const string ProjectUrl = "https://github.com/Cooanyh/wlangohost";
    private readonly UpdateCheckService _updateCheckService;
    private readonly CancellationTokenSource _windowCancellation = new();
    private bool _isClosed;
    private bool _checkRunning;

    internal SettingsWindow(AppViewModel viewModel, UpdateCheckService updateCheckService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _updateCheckService = updateCheckService;
        RefreshLocalizedText();
        UiText.LanguageChanged += UiText_LanguageChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _windowCancellation.Cancel();
            if (!_checkRunning) _windowCancellation.Dispose();
            UiText.LanguageChanged -= UiText_LanguageChanged;
        };
        IconThemeService.ApplyTo(this);
        SystemThemeWatcher.Watch(this);
    }

    private void UiText_LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        CurrentVersionText.Text = UiText.Format("Settings.CurrentVersion", UpdateCheckService.CurrentVersion.ToString(3));
        UpdateStatusText.Text = string.Empty;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        _checkRunning = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = UiText.Get("Update.Checking");

        try
        {
            var release = await _updateCheckService.GetLatestReleaseAsync(_windowCancellation.Token);
            if (_isClosed) return;
            var currentVersion = UpdateCheckService.CurrentVersion;
            if (release.Version <= currentVersion)
            {
                UpdateStatusText.Text = UiText.Format("Update.Current", currentVersion.ToString(3));
                return;
            }

            UpdateStatusText.Text = UiText.Format("Update.Available", release.Tag);
            if (DataContext is AppViewModel existingViewModel && existingViewModel.IsUpdateVersionIgnored(release.Tag))
                return;

            var notice = new UpdateNoticeWindow(release) { Owner = this };
            notice.ShowDialog();
            if (notice.IgnoreThisVersion && DataContext is AppViewModel viewModel)
            {
                if (!viewModel.TryIgnoreUpdateVersion(release.Tag))
                    UpdateStatusText.Text = UiText.Get("Update.IgnoreSaveFailed");
            }
        }
        catch (OperationCanceledException) when (_isClosed)
        {
        }
        catch (Exception exception)
        {
            if (_isClosed) return;
            UpdateStatusText.Text = UiText.Get("Update.Failed");
            BoundedAppLog.Warning("update.manual-check-failed", exception.GetType().Name);
        }
        finally
        {
            _checkRunning = false;
            if (_isClosed)
                _windowCancellation.Dispose();
            else
                CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(ReleasesUrl);

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            UpdateStatusText.Text = UiText.Get("Update.OpenFailed");
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
