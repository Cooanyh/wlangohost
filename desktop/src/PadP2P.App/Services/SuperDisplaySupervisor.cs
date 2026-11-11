using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using System.Windows;

namespace PadP2P.App.Services;

internal sealed class SuperDisplaySupervisor : IAsyncDisposable
{
    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StableRunPeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WindowDiscoveryTimeout = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<DateTimeOffset> _restartHistory = new();
    private Process? _trackedProcess;
    private Process? _retiredProcess;
    private DateTimeOffset _trackedStartedAt;
    private string? _lastExecutablePath;
    private int _disposed;
    private int _autoRestartEnabled;

    public event Action<string, bool>? StatusChanged;

    public async Task OpenHiddenAsync(string executablePath)
    {
        await _operationGate.WaitAsync(_lifetime.Token);
        try
        {
            ThrowIfDisposed();
            Volatile.Write(ref _autoRestartEnabled, 1);
            if (TryGetTrackedProcess(out var tracked))
            {
                if (!await HideMainWindowAsync(tracked, _lifetime.Token))
                    throw new InvalidOperationException(UiText.Get("SuperDisplay.HideFailedRunning"));

                Publish(UiText.Get("SuperDisplay.RunningHidden"));
                return;
            }

            executablePath = FindExecutablePath(executablePath)
                ?? throw new FileNotFoundException(UiText.Get("SuperDisplay.NotFound"));
            _lastExecutablePath = executablePath;
            _restartHistory.Clear();

            var existing = FindRunningProcess(executablePath);
            if (existing is not null)
            {
                TrackProcess(existing);
                if (!await HideMainWindowAsync(existing, _lifetime.Token))
                    throw new InvalidOperationException(UiText.Get("SuperDisplay.HideFailedRunning"));

                BoundedAppLog.Info("superdisplay.adopted-existing-process");
                Publish(UiText.Get("SuperDisplay.AdoptedHidden"));
                return;
            }

            await LaunchAndHideAsync(executablePath, isRestart: false, _lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BoundedAppLog.Error("superdisplay.open-failed", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void DisableAutoRestart() => Volatile.Write(ref _autoRestartEnabled, 0);

    private async Task LaunchAndHideAsync(string executablePath, bool isRestart, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            },
            EnableRaisingEvents = false
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException(UiText.Get("SuperDisplay.StartFailed"));

            TrackProcess(process);
            BoundedAppLog.Info(isRestart ? "superdisplay.restart-started" : "superdisplay.started");

            var hidden = await HideMainWindowAsync(process, cancellationToken);
            if (!IsAlive(process))
                throw new InvalidOperationException(UiText.Get("SuperDisplay.ExitedDuringStart"));
            if (!hidden)
                throw new InvalidOperationException(UiText.Get("SuperDisplay.HideFailedStarted"));

            Publish(UiText.Get(isRestart ? "SuperDisplay.RestartedHidden" : "SuperDisplay.StartedHidden"));
        }
        catch
        {
            if (!IsTracked(process))
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            throw;
        }
    }

    private void TrackProcess(Process process)
    {
        Process? retired;
        lock (_sync)
        {
            retired = _retiredProcess;
            _retiredProcess = null;
            _trackedProcess = process;
            _trackedStartedAt = DateTimeOffset.UtcNow;
        }
        retired?.Dispose();

        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        if (!IsAlive(process))
            OnProcessExited(process, EventArgs.Empty);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;

        var exitCode = -1;
        try { exitCode = process.ExitCode; }
        catch (InvalidOperationException) { }

        DateTimeOffset startedAt;
        Process? retired;
        lock (_sync)
        {
            if (!ReferenceEquals(_trackedProcess, process))
            {
                if (!ReferenceEquals(_retiredProcess, process))
                    process.Dispose();
                return;
            }

            startedAt = _trackedStartedAt;
            retired = _retiredProcess;
            _retiredProcess = process;
            _trackedProcess = null;
        }

        process.Exited -= OnProcessExited;
        if (retired is not null && !ReferenceEquals(retired, process))
            retired.Dispose();
        BoundedAppLog.Info("superdisplay.exited", $"exitCode={exitCode}");

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _autoRestartEnabled) == 0) return;
        if (exitCode == 0)
        {
            Publish(UiText.Get("SuperDisplay.ExitedNormally"));
            return;
        }

        _ = RecoverAfterAbnormalExitAsync(startedAt, exitCode);
    }

    private async Task RecoverAfterAbnormalExitAsync(DateTimeOffset startedAt, int exitCode)
    {
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token);
            int attempt;
            TimeSpan delay;
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _autoRestartEnabled) == 0 || TryGetTrackedProcess(out _)) return;

                var now = DateTimeOffset.UtcNow;
                if (now - startedAt >= StableRunPeriod)
                    _restartHistory.Clear();
                while (_restartHistory.TryPeek(out var previous) && now - previous >= RestartWindow)
                    _restartHistory.Dequeue();

                if (_restartHistory.Count >= RestartDelays.Length)
                {
                    BoundedAppLog.Warning("superdisplay.restart-limit-reached", $"exitCode={exitCode}");
                    Publish(UiText.Get("SuperDisplay.RestartLimit"), isError: true);
                    return;
                }

                _restartHistory.Enqueue(now);
                attempt = _restartHistory.Count;
                delay = RestartDelays[attempt - 1];
            }
            finally
            {
                _operationGate.Release();
            }

            BoundedAppLog.Warning("superdisplay.abnormal-exit", $"exitCode={exitCode}; retry={attempt}/{RestartDelays.Length}");
            Publish(UiText.Format("SuperDisplay.RestartDelay", delay.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture), attempt, RestartDelays.Length));
            await Task.Delay(delay, _lifetime.Token);
            if (Volatile.Read(ref _autoRestartEnabled) == 0) return;

            await _operationGate.WaitAsync(_lifetime.Token);
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _autoRestartEnabled) == 0 || TryGetTrackedProcess(out _)) return;

                var executablePath = FindExecutablePath(_lastExecutablePath)
                    ?? throw new FileNotFoundException(UiText.Get("SuperDisplay.RestartPathMissing"));
                _lastExecutablePath = executablePath;
                var existing = FindRunningProcess(executablePath);
                if (existing is not null)
                {
                    TrackProcess(existing);
                    if (!await HideMainWindowAsync(existing, _lifetime.Token))
                        throw new InvalidOperationException(UiText.Get("SuperDisplay.HideFailedRunning"));
                    BoundedAppLog.Info("superdisplay.restart-adopted-existing-process");
                    Publish(UiText.Get("SuperDisplay.AdoptedAfterRestart"));
                }
                else
                {
                    await LaunchAndHideAsync(executablePath, isRestart: true, _lifetime.Token);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            BoundedAppLog.Error("superdisplay.restart-failed", exception);
            Publish(UiText.Format("SuperDisplay.RestartFailed", exception.Message), isError: true);
        }
    }

    private static async Task<bool> HideMainWindowAsync(Process process, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        WindowShowHider hider = dispatcher is null || dispatcher.CheckAccess()
            ? new WindowShowHider(process.Id)
            : await dispatcher.InvokeAsync(() => new WindowShowHider(process.Id));

        using (hider)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < WindowDiscoveryTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAlive(process)) return false;

                if (TryFindVisibleTopLevelWindow(process.Id, out var window))
                {
                    NativeMethods.ShowWindow(window, NativeMethods.SwHide);
                    if (!NativeMethods.IsWindowVisible(window)) return true;
                }

                if (hider.WindowHidden) return true;
                await Task.Delay(75, cancellationToken);
            }

            return !TryFindVisibleTopLevelWindow(process.Id, out _);
        }
    }

    private static bool TryFindVisibleTopLevelWindow(int processId, out IntPtr window)
    {
        var foundWindow = IntPtr.Zero;
        window = IntPtr.Zero;
        NativeMethods.EnumWindows((candidate, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(candidate, out var candidateProcessId);
            if (candidateProcessId != (uint)processId || !NativeMethods.IsWindowVisible(candidate))
                return true;
            if (NativeMethods.GetWindow(candidate, NativeMethods.GwOwner) != IntPtr.Zero)
                return true;

            foundWindow = candidate;
            return false;
        }, IntPtr.Zero);
        window = foundWindow;
        return window != IntPtr.Zero;
    }

    private static Process? FindRunningProcess(string executablePath)
    {
        var targetPath = Path.GetFullPath(executablePath);
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(targetPath)))
        {
            try
            {
                var candidatePath = candidate.MainModule?.FileName;
                if (candidatePath is not null &&
                    string.Equals(Path.GetFullPath(candidatePath), targetPath, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }

            candidate.Dispose();
        }

        return null;
    }

    internal static string? FindExecutablePath(string? preferredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredPath))
            candidates.Add(preferredPath);

        foreach (var process in Process.GetProcessesByName("SuperDisplay"))
        {
            try
            {
                var runningPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(runningPath)) candidates.Add(runningPath);
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            finally { process.Dispose(); }
        }

        var uninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(uninstallPath);
                    if (uninstall is null) continue;

                    foreach (var keyName in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(keyName);
                        if (!string.Equals(entry?.GetValue("DisplayName") as string, "SuperDisplay", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var installLocation = entry?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLocation))
                            candidates.Add(Path.Combine(installLocation, "SuperDisplay.exe"));

                        var displayIcon = ParseDisplayIcon(entry?.GetValue("DisplayIcon") as string);
                        if (displayIcon is not null && string.Equals(
                                Path.GetFileName(displayIcon),
                                "SuperDisplay.exe",
                                StringComparison.OrdinalIgnoreCase))
                            candidates.Add(displayIcon);
                    }
                }
                catch (IOException) { }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrWhiteSpace(programFiles))
                candidates.Add(Path.Combine(programFiles, "SuperDisplay", "SuperDisplay.exe"));
        }

        var localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localPrograms))
            candidates.Add(Path.Combine(localPrograms, "Programs", "SuperDisplay", "SuperDisplay.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (string.Equals(Path.GetFileName(fullPath), "SuperDisplay.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                    return fullPath;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }

        return null;
    }

    private static string? ParseDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var icon = value.Trim();
        if (icon.StartsWith('"'))
        {
            var closingQuote = icon.IndexOf('"', 1);
            if (closingQuote > 1) icon = icon[1..closingQuote];
        }
        else
        {
            var comma = icon.LastIndexOf(',');
            if (comma > 0 && int.TryParse(icon[(comma + 1)..], out _))
                icon = icon[..comma];
        }

        return icon.Trim('"');
    }

    private bool TryGetTrackedProcess(out Process process)
    {
        lock (_sync)
        {
            if (_trackedProcess is not null && IsAlive(_trackedProcess))
            {
                process = _trackedProcess;
                return true;
            }
        }

        process = null!;
        return false;
    }

    private bool IsTracked(Process process)
    {
        lock (_sync) return ReferenceEquals(_trackedProcess, process);
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private void Publish(string message, bool isError = false) =>
        StatusChanged?.Invoke(message, isError);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SuperDisplaySupervisor));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _operationGate.WaitAsync();
        try
        {
            Process? process;
            Process? retired;
            lock (_sync)
            {
                process = _trackedProcess;
                retired = _retiredProcess;
                _trackedProcess = null;
                _retiredProcess = null;
            }

            if (process is not null)
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            retired?.Dispose();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class WindowShowHider : IDisposable
    {
        private readonly int _processId;
        private readonly NativeMethods.WinEventDelegate _callback;
        private readonly IntPtr _hook;
        private int _windowHidden;

        public WindowShowHider(int processId)
        {
            _processId = processId;
            _callback = OnWindowEvent;
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EventObjectShow,
                NativeMethods.EventObjectShow,
                IntPtr.Zero,
                _callback,
                (uint)processId,
                0,
                NativeMethods.WineventOutOfContext);
        }

        public bool WindowHidden => Volatile.Read(ref _windowHidden) != 0;

        private void OnWindowEvent(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (eventType != NativeMethods.EventObjectShow ||
                objectId != NativeMethods.ObjIdWindow ||
                childId != NativeMethods.ChildIdSelf ||
                window == IntPtr.Zero)
                return;

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId != _processId ||
                NativeMethods.GetWindow(window, NativeMethods.GwOwner) != IntPtr.Zero)
                return;

            NativeMethods.ShowWindow(window, NativeMethods.SwHide);
            if (!NativeMethods.IsWindowVisible(window))
                Interlocked.Exchange(ref _windowHidden, 1);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(_hook);
            GC.KeepAlive(_callback);
        }
    }

    private static class NativeMethods
    {
        public const uint EventObjectShow = 0x8002;
        public const int ObjIdWindow = 0;
        public const int ChildIdSelf = 0;
        public const uint WineventOutOfContext = 0;
        public const int GwOwner = 4;
        public const int SwHide = 0;

        public delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        public delegate bool EnumWindowsDelegate(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookModule,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr eventHook);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
