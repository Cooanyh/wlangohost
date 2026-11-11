using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace PadP2P.App.Services;

internal sealed class NetworkControlService : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _ackLock = new();
    private readonly HashSet<string> _peerDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _legacyPeerAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Func<NetworkState, bool> Predicate, TaskCompletionSource<NetworkState> Completion)> _stateWaiters = [];
    private readonly string _backendPath = Path.Combine(AppContext.BaseDirectory, "backend", "WlanGO.ControlBackend.exe");
    private NetworkState _state = NetworkState.Stopped;
    private int _connectedCount;
    private int _backendConnectedCount;
    private int _peerEventCount;
    private LegacyPeerMonitor? _legacyPeerMonitor;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCancellation;
    private Task? _readerTask;
    private TaskCompletionSource? _pendingAck;
    private string? _expectedAck;
    private volatile bool _expectedProcessExit;
    private volatile bool _disposed;

    public NetworkState State { get { lock (_stateLock) return _state; } }
    public int ConnectedCount { get { lock (_stateLock) return _connectedCount; } }

    public event Action<NetworkStateChangedEventArgs>? StateChanged;
    public event Action<PeerChangedEventArgs>? PeerChanged;
    public event Action<string>? ErrorReceived;

    public async Task StartNetworkAsync(string ssid, string passphrase, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == NetworkState.Running) return;
            if (State is NetworkState.Starting or NetworkState.Stopping)
                throw new InvalidOperationException("A Wi-Fi Direct operation is already in progress.");

            if (_process is not null && !_process.HasExited && State == NetworkState.Error)
                await ShutdownBackendCoreAsync(cancellationToken).ConfigureAwait(false);

            await EnsureBackendCoreAsync(cancellationToken).ConfigureAwait(false);
            StartLegacyPeerMonitor(ssid);
            var encodedSsid = Convert.ToBase64String(Encoding.UTF8.GetBytes(ssid));
            var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(passphrase));
            await SendAndWaitForAckAsync($"CONFIG|{encodedSsid}|{encodedPassword}|1", "CONFIG", cancellationToken).ConfigureAwait(false);

            var stateTask = WaitForStateAsync(state => state is NetworkState.Running or NetworkState.Error, TimeSpan.FromSeconds(30), cancellationToken);
            Exception? commandError = null;
            try
            {
                await SendAndWaitForAckAsync("START", "START", cancellationToken, TimeSpan.FromSeconds(35)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                commandError = ex;
            }

            if (commandError is not null)
            {
                if (State is not NetworkState.Error)
                {
                    try { await stateTask.ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(commandError).Throw();
            }

            var terminalState = await stateTask.ConfigureAwait(false);
            if (terminalState == NetworkState.Error)
                throw new InvalidOperationException("The Wi-Fi Direct backend reported a start failure.");
        }
        catch
        {
            try
            {
                if (_process is { HasExited: false })
                    await ShutdownBackendCoreAsync(CancellationToken.None).ConfigureAwait(false);
                else
                    await CleanupBackendCoreAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                ErrorReceived?.Invoke("Cleanup after a failed Wi-Fi Direct start did not complete cleanly: " + cleanupException.Message);
            }
            SetState(NetworkState.Error, ConnectedCount);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopNetworkAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == NetworkState.Stopped) return;
            if (_process is null || _process.HasExited)
            {
                SetState(NetworkState.Error, 0);
                throw new InvalidOperationException("The Wi-Fi Direct backend is not running.");
            }

            var stateTask = WaitForStateAsync(state => state is NetworkState.Stopped or NetworkState.Error, TimeSpan.FromSeconds(15), cancellationToken);
            _expectedProcessExit = true;
            Exception? commandError = null;
            try { await SendAndWaitForAckAsync("STOP", "STOP", cancellationToken, TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
            catch (Exception ex) { commandError = ex; }
            NetworkState terminalState;
            try { terminalState = await stateTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                terminalState = NetworkState.Error;
                commandError ??= ex;
            }
            await WaitForBackendExitAsync(cancellationToken).ConfigureAwait(false);
            await CleanupBackendCoreAsync().ConfigureAwait(false);
            if (commandError is not null) throw commandError;
            if (terminalState == NetworkState.Error)
                throw new InvalidOperationException("Wi-Fi Direct reported an error while stopping.");
        }
        catch
        {
            if (State is NetworkState.Stopping or NetworkState.Running)
                SetState(NetworkState.Error, ConnectedCount);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (_process is not null && !_process.HasExited)
                await ShutdownBackendCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _lifecycleGate.Release();
        }
    }

    private async Task EnsureBackendCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _pipe?.IsConnected == true) return;
        await CleanupBackendCoreAsync().ConfigureAwait(false);
        if (!File.Exists(_backendPath))
            throw new FileNotFoundException("The Wi-Fi Direct control backend was not found beside the application.", _backendPath);

        var pipeName = "PadP2P-" + Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(_backendPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_backendPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnBackendExited;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Wi-Fi Direct backend process could not be started.");
        }
        _process = process;
        _expectedProcessExit = false;

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(8000, cancellationToken).ConfigureAwait(false);
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            _readCancellation = new CancellationTokenSource();
            _readerTask = ReadBackendEventsAsync(_readCancellation.Token);
            await WaitForStateAsync(state => state == NetworkState.Stopped, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            await CleanupBackendCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ShutdownBackendCoreAsync(CancellationToken cancellationToken)
    {
        _expectedProcessExit = true;
        try
        {
            if (_pipe?.IsConnected == true)
                await SendAndWaitForAckAsync("SHUTDOWN", "SHUTDOWN", cancellationToken, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            ErrorReceived?.Invoke("Backend shutdown did not complete cleanly: " + ex.Message);
        }

        if (_process is { HasExited: false })
        {
            try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { ErrorReceived?.Invoke("The backend process did not exit after cleanup was requested."); }
            }
        }
        await CleanupBackendCoreAsync().ConfigureAwait(false);
        if (State != NetworkState.Error) SetState(NetworkState.Stopped, 0);
    }

    private async Task SendAndWaitForAckAsync(string command, string expectedAck, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_ackLock)
        {
            _expectedAck = expectedAck;
            _pendingAck = completion;
        }

        try
        {
            var writer = _writer ?? throw new IOException("The backend pipe is not connected.");
            await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_ackLock)
            {
                if (ReferenceEquals(_pendingAck, completion))
                {
                    _pendingAck = null;
                    _expectedAck = null;
                }
            }
            _commandGate.Release();
        }
    }

    private async Task<NetworkState> WaitForStateAsync(Func<NetworkState, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<NetworkState> waitTask;
        lock (_stateLock)
        {
            if (predicate(_state)) return _state;
            var completion = new TaskCompletionSource<NetworkState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stateWaiters.Add((predicate, completion));
            waitTask = completion.Task;
        }
        try { return await waitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_stateLock) _stateWaiters.RemoveAll(waiter => waiter.Completion.Task == waitTask);
        }
    }

    private async Task ReadBackendEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = _reader;
            if (reader is null) return;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                HandleBackendMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or DecoderFallbackException)
        {
            if (!_expectedProcessExit) ErrorReceived?.Invoke("Backend IPC failed: " + ex.Message);
        }
        finally
        {
            if (!_expectedProcessExit && !_disposed && State is not NetworkState.Error)
                SetState(NetworkState.Error, ConnectedCount);
        }
    }

    private void HandleBackendMessage(string line)
    {
        var fields = line.Split('|');
        if (fields.Length == 0) return;
        switch (fields[0])
        {
            case "STATE" when fields.Length == 3 && int.TryParse(fields[2], out var count):
                if (Enum.TryParse<NetworkState>(fields[1], true, out var state)) SetState(state, Math.Max(0, count));
                break;
            case "PEER_CONNECTED" when fields.Length == 2:
                HandlePeerEvent(connected: true, DecodeBase64(fields[1]));
                break;
            case "PEER_DISCONNECTED" when fields.Length == 2:
                HandlePeerEvent(connected: false, DecodeBase64(fields[1]));
                break;
            case "ERROR" when fields.Length == 2:
                var message = DecodeBase64(fields[1]);
                ErrorReceived?.Invoke(message);
                lock (_ackLock) _pendingAck?.TrySetException(new InvalidOperationException(message));
                break;
            case "ACK" when fields.Length == 2:
                lock (_ackLock)
                    if (string.Equals(fields[1], _expectedAck, StringComparison.Ordinal)) _pendingAck?.TrySetResult();
                break;
        }
    }

    private void HandlePeerEvent(bool connected, string deviceInfo)
    {
        NetworkState state;
        int count;
        lock (_stateLock)
        {
            if (connected)
            {
                var added = string.IsNullOrWhiteSpace(deviceInfo) || _peerDisplayNames.Add(deviceInfo);
                if (added) _peerEventCount++;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(deviceInfo) && !_peerDisplayNames.Remove(deviceInfo) && _peerDisplayNames.Count > 0)
                    _peerDisplayNames.Remove(_peerDisplayNames.First());
                else if (string.IsNullOrWhiteSpace(deviceInfo) && _peerDisplayNames.Count > 0)
                    _peerDisplayNames.Remove(_peerDisplayNames.First());
                _peerEventCount = Math.Max(0, _peerEventCount - 1);
            }

            state = _state;
            count = _connectedCount = Math.Max(_backendConnectedCount, Math.Max(_peerEventCount, _legacyPeerAddresses.Count));
        }

        PeerChanged?.Invoke(new PeerChangedEventArgs(connected, deviceInfo));
        StateChanged?.Invoke(new NetworkStateChangedEventArgs(state, count));
        BoundedAppLog.Info("wifi-direct.peer-event", $"connected={connected}; count={count}");
    }

    private void SetState(NetworkState state, int connectedCount)
    {
        List<TaskCompletionSource<NetworkState>> completed = [];
        LegacyPeerMonitor? monitorToStop = null;
        int effectiveCount;
        lock (_stateLock)
        {
            _state = state;
            if (state is NetworkState.Stopped or NetworkState.Error)
            {
                monitorToStop = _legacyPeerMonitor;
                _legacyPeerMonitor = null;
                _legacyPeerAddresses.Clear();
                _backendConnectedCount = 0;
                _peerEventCount = 0;
                _peerDisplayNames.Clear();
                _connectedCount = 0;
            }
            else
            {
                _backendConnectedCount = Math.Max(0, connectedCount);
                _connectedCount = Math.Max(_backendConnectedCount, Math.Max(_peerEventCount, _legacyPeerAddresses.Count));
            }
            effectiveCount = _connectedCount;
            foreach (var waiter in _stateWaiters)
                if (waiter.Predicate(state)) completed.Add(waiter.Completion);
        }
        monitorToStop?.Dispose();
        foreach (var waiter in completed) waiter.TrySetResult(state);
        StateChanged?.Invoke(new NetworkStateChangedEventArgs(state, effectiveCount));
    }

    private void StartLegacyPeerMonitor(string ssid)
    {
        var monitor = new LegacyPeerMonitor(ssid);
        monitor.PeersChanged += addresses => HandleLegacyPeersChanged(monitor, addresses);
        LegacyPeerMonitor? previous;
        lock (_stateLock)
        {
            previous = _legacyPeerMonitor;
            _legacyPeerMonitor = monitor;
        }
        previous?.Dispose();
        monitor.Start();
    }

    private void HandleLegacyPeersChanged(LegacyPeerMonitor source, IReadOnlyCollection<string> addresses)
    {
        string[] added;
        string[] removed;
        NetworkState state;
        int count;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_legacyPeerMonitor, source)) return;
            var next = addresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            added = next.Except(_legacyPeerAddresses, StringComparer.OrdinalIgnoreCase).ToArray();
            removed = _legacyPeerAddresses.Except(next, StringComparer.OrdinalIgnoreCase).ToArray();
            _legacyPeerAddresses.Clear();
            _legacyPeerAddresses.UnionWith(next);
            state = _state;
            count = _connectedCount = Math.Max(_backendConnectedCount, Math.Max(_peerEventCount, _legacyPeerAddresses.Count));
        }

        foreach (var address in removed) PeerChanged?.Invoke(new PeerChangedEventArgs(false, address));
        foreach (var address in added) PeerChanged?.Invoke(new PeerChangedEventArgs(true, address));
        StateChanged?.Invoke(new NetworkStateChangedEventArgs(state, count));
        BoundedAppLog.Info("connected-device-count.changed", $"count={count}");
    }

    private void OnBackendExited(object? sender, EventArgs e)
    {
        if (!_expectedProcessExit && !_disposed)
        {
            var code = -1;
            try { code = _process?.ExitCode ?? -1; } catch (InvalidOperationException) { }
            var message = $"Wi-Fi Direct backend exited unexpectedly (code {code}). You can start it again.";
            ErrorReceived?.Invoke(message);
            SetState(NetworkState.Error, ConnectedCount);
        }
    }

    private async Task WaitForBackendExitAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null) return;
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CleanupBackendCoreAsync()
    {
        _readCancellation?.Cancel();
        _pipe?.Dispose();
        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _reader?.Dispose();
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _pipe?.Dispose();
        _readCancellation?.Dispose();
        _readCancellation = null;
        _readerTask = null;
        _reader = null;
        _writer = null;
        _pipe = null;
        if (_process is not null)
        {
            _process.Exited -= OnBackendExited;
            _process.Dispose();
            _process = null;
        }
    }

    private static string DecodeBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetworkControlService));
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
