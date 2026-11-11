using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using PadP2P.App.Services;
using PadP2P.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class SettingsWindow : FluentWindow
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Cooanyh/wlangohost/releases/latest";
    private const string ReleasesUrl = "https://github.com/Cooanyh/wlangohost/releases/latest";
    private const string ProjectUrl = "https://github.com/Cooanyh/wlangohost";
    private static readonly HttpClient UpdateClient = CreateUpdateClient();

    internal SettingsWindow(AppViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        RefreshLocalizedText();
        UiText.LanguageChanged += UiText_LanguageChanged;
        Closed += (_, _) => UiText.LanguageChanged -= UiText_LanguageChanged;
        IconThemeService.ApplyTo(this);
        SystemThemeWatcher.Watch(this);
    }

    private void UiText_LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        CurrentVersionText.Text = UiText.Format("Settings.CurrentVersion", CurrentVersion().ToString(3));
        UpdateStatusText.Text = string.Empty;
    }

    private static HttpClient CreateUpdateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WlanGO/{CurrentVersion().ToString(3)}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version CurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = UiText.Get("Update.Checking");

        try
        {
            using var response = await UpdateClient.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var release = await JsonDocument.ParseAsync(stream);

            if (!release.RootElement.TryGetProperty("tag_name", out var tagProperty) ||
                tagProperty.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The GitHub release response did not contain a version tag.");
            }

            var tag = tagProperty.GetString() ?? string.Empty;
            var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            if (!Version.TryParse(versionText, out var latestVersion))
            {
                throw new InvalidDataException("The latest GitHub release tag is not a stable version.");
            }

            var currentVersion = CurrentVersion();
            UpdateStatusText.Text = latestVersion > currentVersion
                ? UiText.Format("Update.Available", tag)
                : UiText.Format("Update.Current", currentVersion.ToString(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            UpdateStatusText.Text = UiText.Get("Update.Failed");
        }
        finally
        {
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
