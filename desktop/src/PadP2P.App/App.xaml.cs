using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using H.NotifyIcon;
using PadP2P.App.Models;
using PadP2P.App.Services;
using PadP2P.App.ViewModels;
using Wpf.Ui.Appearance;

namespace PadP2P.App;

public partial class App : Application
{
    private const string MutexPrefix = "Local\\PadP2P.Desktop.";
    private const string ActivationPrefix = "Local\\PadP2P.Activate.";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private TaskbarIcon? _trayIcon;
    private AppViewModel? _viewModel;
    private MenuItem? _statusMenuItem;
    private MenuItem? _toggleNetworkMenuItem;
    private MenuItem? _openWindowMenuItem;
    private MenuItem? _launchAppsMenuItem;
    private MenuItem? _copySsidMenuItem;
    private MenuItem? _copyPasswordMenuItem;
    private MenuItem? _startWithWindowsMenuItem;
    private MenuItem? _autoStartNetworkMenuItem;
    private MenuItem? _settingsMenuItem;
    private MenuItem? _exitMenuItem;
    private bool _hasMutex;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (!AcquireSingleInstance())
            {
                Shutdown();
                return;
            }

            ApplicationThemeManager.ApplySystemTheme();
            ApplicationThemeManager.Changed += OnApplicationThemeChanged;
            var settingsStore = new SettingsStore();
            var settings = settingsStore.Load();
            UiText.Language = settings.Language;
            var startup = new StartupRegistrationService();
            startup.EnsureEnabled(settings.StartWithWindows);

            _viewModel = new AppViewModel(settings, settingsStore, startup, new SuperDisplaySupervisor());
            _viewModel.SettingsRequested += OpenSettings;
            _viewModel.ExitRequested += ExitApplicationAsync;
            MainWindow = new MainWindow(_viewModel);
            CreateTrayIcon();
            _viewModel.PropertyChanged += (_, _) => UpdateTrayPresentation();

            var startHidden = e.Args.Any(argument => string.Equals(argument, "--start-hidden", StringComparison.OrdinalIgnoreCase));
            if (!startHidden) ShowMainWindow();

            if (settings.AutoStartNetwork)
                await _viewModel.StartNetworkAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"{UiText.Get("Startup.Failed")}\n\n{GetStartupErrorDetails(exception)}",
                UiText.Get("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static string GetStartupErrorDetails(Exception exception)
    {
        Exception? xamlError = exception;
        while (xamlError is not null && xamlError is not XamlParseException)
            xamlError = xamlError.InnerException;

        var rootCause = exception.GetBaseException();
        var details = rootCause.Message;
        if (xamlError is XamlParseException { LineNumber: > 0 } parseError)
            details = $"{UiText.Format("Startup.XamlLine", parseError.LineNumber, parseError.LinePosition)}\n{details}";

        return details;
    }

    private bool AcquireSingleInstance()
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16];
        var mutexName = MutexPrefix + suffix;
        var eventName = ActivationPrefix + suffix;
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var activation = EventWaitHandle.OpenExisting(eventName);
                activation.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return false;
        }

        _hasMutex = true;
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private void CreateTrayIcon()
    {
        if (_viewModel is null) return;
        var menu = new ContextMenu();
        _statusMenuItem = new MenuItem { IsEnabled = false, FontWeight = FontWeights.SemiBold };
        _toggleNetworkMenuItem = new MenuItem { Header = UiText.Get("Action.Start") };
        _toggleNetworkMenuItem.Click += (_, _) => _viewModel.ToggleNetworkCommand.Execute(null);
        _openWindowMenuItem = new MenuItem { Header = UiText.Get("Tray.OpenWindow") };
        _openWindowMenuItem.Click += (_, _) => ShowMainWindow();
        _launchAppsMenuItem = new MenuItem { Header = UiText.Get("Tray.LaunchApps") };
        _copySsidMenuItem = new MenuItem { Header = UiText.Get("Tray.CopySsid") };
        _copySsidMenuItem.Click += (_, _) => _viewModel.CopySsidCommand.Execute(null);
        _copyPasswordMenuItem = new MenuItem { Header = UiText.Get("Tray.CopyPassword") };
        _copyPasswordMenuItem.Click += (_, _) => _viewModel.CopyPasswordCommand.Execute(null);
        _startWithWindowsMenuItem = new MenuItem { Header = UiText.Get("Tray.StartWithWindows"), IsCheckable = true };
        _startWithWindowsMenuItem.Click += (_, _) => _viewModel.StartWithWindows = _startWithWindowsMenuItem.IsChecked;
        _autoStartNetworkMenuItem = new MenuItem { Header = UiText.Get("Tray.AutoStartNetwork"), IsCheckable = true };
        _autoStartNetworkMenuItem.Click += (_, _) => _viewModel.AutoStartNetwork = _autoStartNetworkMenuItem.IsChecked;
        _settingsMenuItem = new MenuItem { Header = UiText.Get("Tray.Settings") };
        _settingsMenuItem.Click += (_, _) => OpenSettings();
        _exitMenuItem = new MenuItem { Header = UiText.Get("Tray.Exit") };
        _exitMenuItem.Click += async (_, _) => await ExitApplicationAsync();

        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleNetworkMenuItem);
        menu.Items.Add(_openWindowMenuItem);
        menu.Items.Add(_launchAppsMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_copySsidMenuItem);
        menu.Items.Add(_copyPasswordMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_startWithWindowsMenuItem);
        menu.Items.Add(_autoStartNetworkMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_settingsMenuItem);
        menu.Items.Add(_exitMenuItem);

        _trayIcon = new TaskbarIcon
        {
            IconSource = IconThemeService.LoadStateIcon("Stopped", IconThemeService.IsDark),
            ToolTipText = UiText.Format("Tray.Tooltip", UiText.Get("Status.Stopped")),
            ContextMenu = menu
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _trayIcon.ForceCreate(enablesEfficiencyMode: true);
        UpdateTrayPresentation();
    }

    private void UpdateTrayPresentation()
    {
        if (_viewModel is null) return;
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _viewModel.TrayTooltip;
            _trayIcon.IconSource = IconThemeService.LoadStateIcon(_viewModel.TrayIconName, IconThemeService.IsDark);
        }
        if (_statusMenuItem is not null) _statusMenuItem.Header = UiText.Format("Tray.Status", _viewModel.TrayStatus);
        if (_toggleNetworkMenuItem is not null)
        {
            _toggleNetworkMenuItem.Header = _viewModel.ActionText;
            _toggleNetworkMenuItem.IsEnabled = _viewModel.CanToggleNetwork;
        }
        if (_openWindowMenuItem is not null) _openWindowMenuItem.Header = UiText.Get("Tray.OpenWindow");
        if (_copySsidMenuItem is not null) _copySsidMenuItem.Header = UiText.Get("Tray.CopySsid");
        if (_copyPasswordMenuItem is not null) _copyPasswordMenuItem.Header = UiText.Get("Tray.CopyPassword");
        if (_startWithWindowsMenuItem is not null) _startWithWindowsMenuItem.IsChecked = _viewModel.StartWithWindows;
        if (_autoStartNetworkMenuItem is not null) _autoStartNetworkMenuItem.IsChecked = _viewModel.AutoStartNetwork;
        UpdateLauncherTrayMenu();
        if (_startWithWindowsMenuItem is not null) _startWithWindowsMenuItem.Header = UiText.Get("Tray.StartWithWindows");
        if (_autoStartNetworkMenuItem is not null) _autoStartNetworkMenuItem.Header = UiText.Get("Tray.AutoStartNetwork");
        if (_settingsMenuItem is not null) _settingsMenuItem.Header = UiText.Get("Tray.Settings");
        if (_exitMenuItem is not null) _exitMenuItem.Header = UiText.Get("Tray.Exit");
    }

    private void UpdateLauncherTrayMenu()
    {
        if (_launchAppsMenuItem is null || _viewModel is null) return;
        var entries = _viewModel.LauncherEntries
            .Where(entry => _viewModel.ShowLaunchers && entry.IsEnabled)
            .ToList();
        _launchAppsMenuItem.Header = UiText.Get("Tray.LaunchApps");
        _launchAppsMenuItem.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _launchAppsMenuItem.Items.Clear();

        if (entries.Count > 1 && _viewModel.MultipleLaunchMode == "AllEnabled")
        {
            var openAll = new MenuItem { Header = UiText.Get("Tray.LaunchAll") };
            openAll.Click += (_, _) => _viewModel.LaunchAllEnabledCommand.Execute(null);
            _launchAppsMenuItem.Items.Add(openAll);
            _launchAppsMenuItem.Items.Add(new Separator());
        }
        else if (entries.Count > 1)
        {
            var choose = new MenuItem { Header = UiText.Get("Tray.SelectLaunchers") };
            choose.Click += (_, _) => _viewModel.ChooseLaunchersCommand.Execute(null);
            _launchAppsMenuItem.Items.Add(choose);
            _launchAppsMenuItem.Items.Add(new Separator());
        }

        foreach (var entry in entries)
        {
            var title = string.IsNullOrWhiteSpace(entry.Name)
                ? Path.GetFileNameWithoutExtension(entry.ExecutablePath)
                : entry.Name;
            var menuEntry = new MenuItem { Header = title, ToolTip = entry.ExecutablePath };
            menuEntry.Click += (_, _) => _viewModel.LaunchSingleEntryCommand.Execute(entry);
            _launchAppsMenuItem.Items.Add(menuEntry);
        }
    }

    private void OnApplicationThemeChanged(ApplicationTheme theme, Color systemAccent)
    {
        _ = Dispatcher.InvokeAsync(() => ApplyThemeIcons(theme));
    }

    private void ApplyThemeIcons(ApplicationTheme theme)
    {
        var isDark = theme == ApplicationTheme.Dark;
        foreach (Window window in Windows)
        {
            if (window is MainWindow mainWindow)
                mainWindow.ApplyIconTheme(isDark);
            else
                IconThemeService.ApplyTo(window, isDark);
        }

        if (_viewModel is not null && _trayIcon is not null)
            _trayIcon.IconSource = IconThemeService.LoadStateIcon(_viewModel.TrayIconName, isDark);
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.ShowInTaskbar = true;
        if (!MainWindow.IsVisible) MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void OpenSettings()
    {
        if (_viewModel is null) return;
        var settingsWindow = new SettingsWindow(_viewModel);
        if (MainWindow?.IsVisible == true)
            settingsWindow.Owner = MainWindow;
        else
            settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        settingsWindow.ShowDialog();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting || _viewModel is null) return;
        if (_viewModel.ConnectedDevices.Count > 0)
        {
            var answer = MessageBox.Show(
                MainWindow?.IsVisible == true ? MainWindow : null,
                UiText.Format("Prompt.ExitConnected", _viewModel.ConnectedDevices.Count),
                UiText.Get("Prompt.ExitTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _isExiting = true;
        try
        {
            await _viewModel.ShutdownNetworkAsync();
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"{UiText.Get("Prompt.CleanupFailed")}\n\n{exception.Message}",
                UiText.Get("Prompt.CleanupTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            if (MainWindow is MainWindow mainWindow)
            {
                mainWindow.AllowApplicationExit = true;
                mainWindow.Close();
            }
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        if (_hasMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
