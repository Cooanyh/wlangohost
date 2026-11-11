using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PadP2P.App.Infrastructure;
using PadP2P.App.Models;
using PadP2P.App.Services;

namespace PadP2P.App.ViewModels;

internal sealed class AppViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly UserSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly StartupRegistrationService _startup;
    private readonly NetworkControlService _network = new();
    private readonly SuperDisplaySupervisor _superDisplay;
    private NetworkState _state = NetworkState.Stopped;
    private int _connectedCount;
    private bool _isBusy;
    private bool _isPasswordVisible;
    private string _statusMessage = UiText.Get("Network.ReadyMessage");
    private string _errorMessage = string.Empty;
    private bool _startWithWindows;
    private bool _autoStartNetwork;
    private bool _showLaunchers;
    private string _multipleLaunchMode;
    private bool _isLaunchingApps;

    public AppViewModel(
        UserSettings settings,
        SettingsStore settingsStore,
        StartupRegistrationService startup,
        SuperDisplaySupervisor superDisplay)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _startup = startup;
        _superDisplay = superDisplay;
        _startWithWindows = settings.StartWithWindows;
        _autoStartNetwork = settings.AutoStartNetwork;
        _showLaunchers = settings.ShowLaunchers;
        _multipleLaunchMode = settings.MultipleLaunchMode;
        foreach (var entry in settings.Launchers)
        {
            LauncherEntries.Add(entry);
            entry.PropertyChanged += OnLauncherEntryPropertyChanged;
        }
        LauncherEntries.CollectionChanged += OnLauncherEntriesChanged;

        ToggleNetworkCommand = new AsyncCommand(ToggleNetworkAsync, () => CanToggleNetwork, HandleCommandError);
        OpenLaunchersCommand = new AsyncCommand(OpenLaunchersAsync, () => CanOpenLaunchers, HandleCommandError);
        ChooseLaunchersCommand = new AsyncCommand(ChooseLaunchersAsync, () => CanOpenLaunchers, HandleCommandError);
        LaunchAllEnabledCommand = new AsyncCommand(() => LaunchEntriesAsync(GetEnabledLauncherEntries()), () => CanOpenLaunchers, HandleCommandError);
        LaunchSingleEntryCommand = new RelayCommand(parameter =>
        {
            if (parameter is LauncherEntry entry) _ = LaunchEntriesAsync([entry]);
        });
        CopySsidCommand = new RelayCommand(_ => CopySsid());
        CopyPasswordCommand = new RelayCommand(_ => CopyPassword());
        TogglePasswordVisibilityCommand = new RelayCommand(_ => IsPasswordVisible = !IsPasswordVisible);
        EditNetworkProfileCommand = new RelayCommand(_ => NetworkProfileRequested?.Invoke(), () => CanEditNetworkProfile);
        AddLauncherEntryCommand = new RelayCommand(_ => AddLauncherEntry());
        SelectLauncherEntryPathCommand = new RelayCommand(parameter =>
        {
            if (parameter is LauncherEntry entry) SelectLauncherEntryPath(entry);
        });
        RemoveLauncherEntryCommand = new RelayCommand(parameter =>
        {
            if (parameter is LauncherEntry entry) RemoveLauncherEntry(entry);
        });
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke());
        ExitCommand = new AsyncCommand(() => ExitRequested?.Invoke() ?? Task.CompletedTask, onError: HandleCommandError);

        _network.StateChanged += OnNetworkStateChanged;
        _network.PeerChanged += OnPeerChanged;
        _network.ErrorReceived += OnNetworkError;
        _superDisplay.StatusChanged += OnSuperDisplayStatusChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        if (settings.LastNetworkState.Equals(nameof(NetworkState.Running), StringComparison.OrdinalIgnoreCase) && !settings.AutoStartNetwork)
            _statusMessage = UiText.Get("Network.PreviousRunMessage");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SettingsRequested;
    public event Action? NetworkProfileRequested;
    public event Func<Task>? ExitRequested;
    public ObservableCollection<string> ConnectedDevices { get; } = [];
    public ObservableCollection<LauncherEntry> LauncherEntries { get; } = [];
    public AsyncCommand ToggleNetworkCommand { get; }
    public AsyncCommand OpenLaunchersCommand { get; }
    public AsyncCommand ChooseLaunchersCommand { get; }
    public AsyncCommand LaunchAllEnabledCommand { get; }
    public RelayCommand LaunchSingleEntryCommand { get; }
    public RelayCommand CopySsidCommand { get; }
    public RelayCommand CopyPasswordCommand { get; }
    public RelayCommand TogglePasswordVisibilityCommand { get; }
    public RelayCommand EditNetworkProfileCommand { get; }
    public RelayCommand AddLauncherEntryCommand { get; }
    public RelayCommand SelectLauncherEntryPathCommand { get; }
    public RelayCommand RemoveLauncherEntryCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public AsyncCommand ExitCommand { get; }

    public string Ssid => _settings.Ssid;
    public string Passphrase => _settings.Passphrase;
    public bool CanEditNetworkProfile => !IsBusy && _state == NetworkState.Stopped;
    public bool ShowLaunchers
    {
        get => _showLaunchers;
        set
        {
            if (!SetField(ref _showLaunchers, value)) return;
            _settings.ShowLaunchers = value;
            PersistSettings();
            RefreshLauncherPresentation();
        }
    }
    public string MultipleLaunchMode
    {
        get => _multipleLaunchMode;
        set
        {
            var mode = value is "AllEnabled" or "Select" ? value : "Select";
            if (!SetField(ref _multipleLaunchMode, mode)) return;
            _settings.MultipleLaunchMode = mode;
            PersistSettings();
            RefreshLauncherPresentation();
        }
    }
    public string LaunchButtonText => GetLaunchButtonText();
    public string LaunchButtonToolTip => GetLaunchButtonToolTip();
    public Visibility LaunchButtonVisibility => ShowLaunchers && GetEnabledLauncherEntries().Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public bool CanOpenLaunchers => !_isLaunchingApps && ShowLaunchers && GetEnabledLauncherEntries().Count > 0;
    public string DisplayPassphrase => IsPasswordVisible ? _settings.Passphrase : new string('•', Math.Clamp(_settings.Passphrase.Length, 8, 63));
    public string StateText => _state switch
    {
        NetworkState.Running => UiText.Get("Network.Started"),
        NetworkState.Starting => UiText.Get("Network.Starting"),
        NetworkState.Stopping => UiText.Get("Network.Stopping"),
        NetworkState.Error => UiText.Get("Network.Error"),
        _ => UiText.Get("Network.Stopped")
    };
    public string ActionText => _state switch
    {
        NetworkState.Running => UiText.Get("Action.Stop"),
        NetworkState.Starting => UiText.Get("Action.Starting"),
        NetworkState.Stopping => UiText.Get("Action.Stopping"),
        _ => UiText.Get("Action.Start")
    };
    public bool IsNetworkRunning => _state == NetworkState.Running;
    public string TrayStatus => _state switch
    {
        NetworkState.Running => UiText.Get("Status.Running"),
        NetworkState.Starting => UiText.Get("Status.Starting"),
        NetworkState.Stopping => UiText.Get("Status.Stopping"),
        NetworkState.Error => UiText.Get("Status.Error"),
        _ => UiText.Get("Status.Stopped")
    };
    public string TrayIconName => _state switch
    {
        NetworkState.Running => "Running",
        NetworkState.Error => "Error",
        _ => "Stopped"
    };
    public string TrayTooltip => UiText.Format("Tray.Tooltip", TrayStatus);
    public string DeviceCountText => UiText.Format("DeviceCount", _connectedCount);
    public Visibility DevicePlaceholderVisibility => _connectedCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetField(ref _errorMessage, value); }
    public Brush StateBrush => _state switch
    {
        NetworkState.Running => new SolidColorBrush(Color.FromRgb(16, 124, 16)),
        NetworkState.Error => new SolidColorBrush(Color.FromRgb(196, 43, 28)),
        NetworkState.Starting or NetworkState.Stopping => new SolidColorBrush(Color.FromRgb(0, 120, 212)),
        _ => SystemColors.GrayTextBrush
    };
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanToggleNetwork));
            OnPropertyChanged(nameof(CanEditNetworkProfile));
            ToggleNetworkCommand.RaiseCanExecuteChanged();
            EditNetworkProfileCommand.RaiseCanExecuteChanged();
        }
    }
    public bool CanToggleNetwork => !IsBusy && _state is not (NetworkState.Starting or NetworkState.Stopping);
    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set
        {
            if (!SetField(ref _isPasswordVisible, value)) return;
            OnPropertyChanged(nameof(DisplayPassphrase));
            OnPropertyChanged(nameof(PasswordVisibilityText));
        }
    }
    public string PasswordVisibilityText => UiText.Get(IsPasswordVisible ? "Hide" : "Show");

    public string Language
    {
        get => _settings.Language;
        set
        {
            var language = value is "zh-CN" or "en-US" ? value : "System";
            if (_settings.Language == language) return;
            _settings.Language = language;
            UiText.Language = language;
            PersistSettings();
            OnPropertyChanged(nameof(Language));
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetField(ref _startWithWindows, value)) return;
            _settings.StartWithWindows = value;
            try
            {
                _startup.SetEnabled(value);
                _settingsStore.Save(_settings);
                StatusMessage = UiText.Get(value ? "Status.AutoStartEnabled" : "Status.AutoStartDisabled");
            }
            catch (Exception ex)
            {
                try { _startup.SetEnabled(!value); } catch { }
                _startWithWindows = !value;
                _settings.StartWithWindows = !value;
                OnPropertyChanged();
                ErrorMessage = ex.Message;
            }
        }
    }

    public bool AutoStartNetwork
    {
        get => _autoStartNetwork;
        set
        {
            if (!SetField(ref _autoStartNetwork, value)) return;
            _settings.AutoStartNetwork = value;
            PersistSettings();
            StatusMessage = UiText.Get(value ? "Status.NetworkAutoStartEnabled" : "Status.NetworkAutoStartDisabled");
        }
    }

    public async Task StartNetworkAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = UiText.Get("Status.StartRequest");
        try
        {
            _settings.LastNetworkState = nameof(NetworkState.Starting);
            PersistSettings();
            await _network.StartNetworkAsync(_settings.Ssid, _settings.Passphrase);
            StatusMessage = UiText.Get("Status.GroupOwnerRunning");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = UiText.Get("Status.StartFailed");
            if (_state is NetworkState.Starting or NetworkState.Stopped) SetState(NetworkState.Error, 0);
        }
        finally { IsBusy = false; }
    }

    public async Task StopNetworkAsync(bool confirmConnectedDevices)
    {
        if (IsBusy) return;
        if (confirmConnectedDevices && _connectedCount > 0)
        {
            var answer = MessageBox.Show(
                GetPromptOwner(),
                UiText.Format("Prompt.StopConnected", _connectedCount),
                UiText.Get("Prompt.StopTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = UiText.Get("Status.StopRequest");
        try
        {
            await _network.StopNetworkAsync();
            _settings.LastNetworkState = nameof(NetworkState.Stopped);
            PersistSettings();
            StatusMessage = UiText.Get("Status.NetworkStopped");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = UiText.Get("Status.StopFailed");
        }
        finally { IsBusy = false; }
    }

    public bool TrySaveNetworkProfile(string ssid, string passphrase, out string errorMessage)
    {
        if (!CanEditNetworkProfile)
        {
            errorMessage = UiText.Get("NetworkProfile.ErrorNetworkMustBeStopped");
            return false;
        }

        if (!NetworkProfileValidator.TryValidate(ssid, passphrase, out var validationError))
        {
            errorMessage = UiText.Get(validationError);
            return false;
        }

        var previousSsid = _settings.Ssid;
        var previousPassphrase = _settings.Passphrase;
        _settings.Ssid = ssid;
        _settings.Passphrase = passphrase;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _settings.Ssid = previousSsid;
            _settings.Passphrase = previousPassphrase;
            errorMessage = UiText.Format("NetworkProfile.ErrorSave", exception.Message);
            ErrorMessage = errorMessage;
            return false;
        }

        OnPropertyChanged(nameof(Ssid));
        OnPropertyChanged(nameof(Passphrase));
        OnPropertyChanged(nameof(DisplayPassphrase));
        ErrorMessage = string.Empty;
        StatusMessage = UiText.Get("NetworkProfile.Saved");
        errorMessage = string.Empty;
        return true;
    }

    public Task ShutdownNetworkAsync() => _network.ShutdownAsync();

    public void ShowStoppedStateAfterExit() => SetState(NetworkState.Stopped, 0);

    private async Task ToggleNetworkAsync()
    {
        if (_state == NetworkState.Running)
            await StopNetworkAsync(confirmConnectedDevices: true);
        else
            await StartNetworkAsync();
    }

    private async Task OpenLaunchersAsync()
    {
        var entries = GetEnabledLauncherEntries();
        if (entries.Count == 0) return;
        if (entries.Count > 1 && MultipleLaunchMode == "Select")
        {
            await ChooseLaunchersAsync();
            return;
        }

        await LaunchEntriesAsync(entries);
    }

    private async Task ChooseLaunchersAsync()
    {
        var entries = GetEnabledLauncherEntries();
        if (entries.Count == 0) return;
        if (entries.Count == 1)
        {
            await LaunchEntriesAsync(entries);
            return;
        }

        var dialog = new LauncherSelectionWindow(entries);
        if (GetPromptOwner() is { } owner)
            dialog.Owner = owner;
        if (dialog.ShowDialog() == true)
            await LaunchEntriesAsync(dialog.SelectedEntries);
    }

    private async Task LaunchEntriesAsync(IEnumerable<LauncherEntry> entries)
    {
        List<LauncherEntry> selected = ShowLaunchers
            ? entries.Where(entry => entry.IsEnabled).DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<LauncherEntry>();
        if (selected.Count == 0 || _isLaunchingApps) return;

        _isLaunchingApps = true;
        RefreshLauncherPresentation();
        ErrorMessage = string.Empty;
        StatusMessage = UiText.Get("Status.LaunchingApps");
        try
        {
            var results = await Task.WhenAll(selected.Select(TryLaunchEntryAsync));
            var failures = results.Where(result => !result.Started && !result.Cancelled).ToList();
            var started = results.Count(result => result.Started);
            if (failures.Count > 0)
            {
                ErrorMessage = string.Join(Environment.NewLine, failures.Select(result => $"{result.Name}: {result.Error}"));
                StatusMessage = UiText.Get("Status.LaunchFailed");
            }
            else if (started == 0)
            {
                StatusMessage = UiText.Get("Status.LaunchCancelled");
            }
            else
            {
                StatusMessage = UiText.Format("Status.LaunchStarted", started);
            }
        }
        finally
        {
            _isLaunchingApps = false;
            RefreshLauncherPresentation();
        }
    }

    private async Task<(string Name, bool Started, bool Cancelled, string? Error)> TryLaunchEntryAsync(LauncherEntry entry)
    {
        var name = GetLauncherName(entry);
        try
        {
            if (entry.IsSuperDisplay)
            {
                var executablePath = SuperDisplaySupervisor.FindExecutablePath(entry.ExecutablePath);
                if (executablePath is null)
                {
                    var picker = new OpenFileDialog
                    {
                        Title = UiText.Get("Dialog.SuperDisplayTitle"),
                        Filter = UiText.Get("Dialog.SuperDisplayFilter"),
                        FileName = "SuperDisplay.exe",
                        CheckFileExists = true,
                        Multiselect = false
                    };
                    if (picker.ShowDialog(GetPromptOwner()) != true)
                        return (name, Started: false, Cancelled: true, Error: null);
                    executablePath = picker.FileName;
                }

                if (!string.Equals(Path.GetFileName(executablePath), "SuperDisplay.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(UiText.Get("SuperDisplay.PathInvalid"));

                if (!string.Equals(entry.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    entry.ExecutablePath = executablePath;
                    PersistSettings();
                }
                await _superDisplay.OpenHiddenAsync(executablePath);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.ExecutablePath) || !File.Exists(entry.ExecutablePath))
                    throw new FileNotFoundException(UiText.Get("Launcher.PathMissing"));

                var startFailedMessage = UiText.Get("Launcher.StartFailed");
                await Task.Run(() =>
                {
                    var fullPath = Path.GetFullPath(entry.ExecutablePath);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        WorkingDirectory = Path.GetDirectoryName(fullPath)!,
                        UseShellExecute = false
                    };
                    using var process = Process.Start(startInfo)
                        ?? throw new InvalidOperationException(startFailedMessage);
                });
            }

            return (name, Started: true, Cancelled: false, Error: null);
        }
        catch (Exception exception)
        {
            return (name, Started: false, Cancelled: false, Error: exception.Message);
        }
    }

    private void AddLauncherEntry()
    {
        var picker = new OpenFileDialog
        {
            Title = UiText.Get("Dialog.LauncherTitle"),
            Filter = UiText.Get("Dialog.ExecutableFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(GetPromptOwner()) != true) return;

        var isSuperDisplay = string.Equals(Path.GetFileName(picker.FileName), "SuperDisplay.exe", StringComparison.OrdinalIgnoreCase)
            && LauncherEntries.All(entry => !entry.IsSuperDisplay);
        LauncherEntries.Add(new LauncherEntry
        {
            Id = isSuperDisplay ? LauncherEntry.SuperDisplayId : Guid.NewGuid().ToString("N"),
            Name = Path.GetFileNameWithoutExtension(picker.FileName),
            ExecutablePath = picker.FileName,
            IsEnabled = true
        });
    }

    private void SelectLauncherEntryPath(LauncherEntry entry)
    {
        var superDisplay = entry.IsSuperDisplay;
        var picker = new OpenFileDialog
        {
            Title = UiText.Get(superDisplay ? "Dialog.SuperDisplayTitle" : "Dialog.LauncherTitle"),
            Filter = UiText.Get(superDisplay ? "Dialog.SuperDisplayFilter" : "Dialog.ExecutableFilter"),
            FileName = superDisplay ? "SuperDisplay.exe" : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(GetPromptOwner()) != true) return;
        if (superDisplay && !string.Equals(Path.GetFileName(picker.FileName), "SuperDisplay.exe", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = UiText.Get("SuperDisplay.PathInvalid");
            return;
        }

        entry.ExecutablePath = picker.FileName;
        if (string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = Path.GetFileNameWithoutExtension(picker.FileName);
        PersistSettings();
        RefreshLauncherPresentation();
    }

    private void RemoveLauncherEntry(LauncherEntry entry)
    {
        if (!LauncherEntries.Contains(entry)) return;
        if (entry.IsSuperDisplay) _superDisplay.DisableAutoRestart();
        LauncherEntries.Remove(entry);
    }

    private List<LauncherEntry> GetEnabledLauncherEntries() =>
        LauncherEntries.Where(entry => entry.IsEnabled).ToList();

    private string GetLauncherName(LauncherEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name)) return entry.Name.Trim();
        var fileName = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
        return string.IsNullOrWhiteSpace(fileName) ? UiText.Get("Launcher.Untitled") : fileName;
    }

    private string GetLaunchButtonText()
    {
        var names = GetEnabledLauncherEntries().Select(GetLauncherName).ToList();
        if (names.Count == 0) return UiText.Get("Launcher.OpenGeneric");
        if (names.Count == 1) return UiText.Format("Launcher.OpenOne", names[0]);
        if (MultipleLaunchMode == "AllEnabled")
            return UiText.Format("Launcher.OpenCombined", string.Join(UiText.Get("Launcher.CombineSeparator"), names));
        return UiText.Get("Launcher.Select");
    }

    private string GetLaunchButtonToolTip() =>
        string.Join(UiText.Get("Launcher.CombineSeparator"), GetEnabledLauncherEntries().Select(GetLauncherName));

    private void RefreshLauncherPresentation()
    {
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(LaunchButtonToolTip));
        OnPropertyChanged(nameof(LaunchButtonVisibility));
        OnPropertyChanged(nameof(CanOpenLaunchers));
        OpenLaunchersCommand.RaiseCanExecuteChanged();
        ChooseLaunchersCommand.RaiseCanExecuteChanged();
        LaunchAllEnabledCommand.RaiseCanExecuteChanged();
    }

    private void OnLauncherEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (LauncherEntry entry in e.OldItems)
                entry.PropertyChanged -= OnLauncherEntryPropertyChanged;
        if (e.NewItems is not null)
            foreach (LauncherEntry entry in e.NewItems)
                entry.PropertyChanged += OnLauncherEntryPropertyChanged;
        PersistSettings();
        RefreshLauncherPresentation();
    }

    private void OnLauncherEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PersistSettings();
        RefreshLauncherPresentation();
    }

    private void CopySsid()
    {
        Clipboard.SetText(_settings.Ssid);
        StatusMessage = UiText.Get("Status.SsidCopied");
    }

    private void CopyPassword()
    {
        var result = MessageBox.Show(
            GetPromptOwner(),
            UiText.Get("Prompt.CopyPassword"),
            UiText.Get("Prompt.CopyPasswordTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        Clipboard.SetText(_settings.Passphrase);
        StatusMessage = UiText.Get("Status.PasswordCopied");
    }

    private void OnNetworkStateChanged(NetworkStateChangedEventArgs e) => OnUi(() =>
    {
        SetState(e.State, e.ConnectedCount);
        if (e.State is NetworkState.Running or NetworkState.Stopped or NetworkState.Error)
        {
            _settings.LastNetworkState = e.State.ToString();
            PersistSettings();
        }
    });

    private void OnPeerChanged(PeerChangedEventArgs e) => OnUi(() =>
    {
        if (e.Connected)
        {
            if (!string.IsNullOrWhiteSpace(e.DeviceInfo) && !ConnectedDevices.Contains(e.DeviceInfo))
                ConnectedDevices.Add(e.DeviceInfo);
        }
        else if (!string.IsNullOrWhiteSpace(e.DeviceInfo) && ConnectedDevices.Contains(e.DeviceInfo))
            ConnectedDevices.Remove(e.DeviceInfo);
        else if (ConnectedDevices.Count > _connectedCount)
            ConnectedDevices.RemoveAt(0);
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(DevicePlaceholderVisibility));
    });

    private void OnNetworkError(string message) => OnUi(() =>
    {
        ErrorMessage = message;
        StatusMessage = UiText.Get("Status.BackendError");
    });

    private void OnSuperDisplayStatusChanged(string message, bool isError) => OnUi(() =>
    {
        StatusMessage = message;
        ErrorMessage = isError ? message : string.Empty;
    });

    private void SetState(NetworkState state, int count)
    {
        _state = state;
        _connectedCount = count;
        if (state == NetworkState.Stopped) ConnectedDevices.Clear();
        if (ConnectedDevices.Count > count)
            while (ConnectedDevices.Count > count) ConnectedDevices.RemoveAt(0);
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsNetworkRunning));
        OnPropertyChanged(nameof(TrayStatus));
        OnPropertyChanged(nameof(TrayIconName));
        OnPropertyChanged(nameof(TrayTooltip));
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(DevicePlaceholderVisibility));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(CanToggleNetwork));
        OnPropertyChanged(nameof(CanEditNetworkProfile));
        ToggleNetworkCommand.RaiseCanExecuteChanged();
        EditNetworkProfileCommand.RaiseCanExecuteChanged();
    }

    private void PersistSettings()
    {
        _settings.Launchers = LauncherEntries.ToList();
        _settings.ShowLaunchers = _showLaunchers;
        _settings.MultipleLaunchMode = _multipleLaunchMode;
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) { ErrorMessage = "Settings could not be saved: " + ex.Message; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (_state == NetworkState.Running)
            StatusMessage = UiText.Get("Status.GroupOwnerRunning");
        else if (_state == NetworkState.Stopped && _settings.LastNetworkState == nameof(NetworkState.Running) && !_settings.AutoStartNetwork)
            StatusMessage = UiText.Get("Network.PreviousRunMessage");
        else
            StatusMessage = UiText.Get("Network.ReadyMessage");
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(TrayStatus));
        OnPropertyChanged(nameof(TrayTooltip));
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(PasswordVisibilityText));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(LaunchButtonToolTip));
    });

    private void HandleCommandError(Exception exception)
    {
        ErrorMessage = exception.Message;
        StatusMessage = UiText.Get("Status.CommandFailed");
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static Window? GetPromptOwner() => Application.Current.MainWindow is { IsVisible: true } window ? window : null;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public async ValueTask DisposeAsync()
    {
        _network.StateChanged -= OnNetworkStateChanged;
        _network.PeerChanged -= OnPeerChanged;
        _network.ErrorReceived -= OnNetworkError;
        _superDisplay.StatusChanged -= OnSuperDisplayStatusChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        LauncherEntries.CollectionChanged -= OnLauncherEntriesChanged;
        foreach (var entry in LauncherEntries)
            entry.PropertyChanged -= OnLauncherEntryPropertyChanged;
        await _superDisplay.DisposeAsync();
        await _network.DisposeAsync();
    }
}
