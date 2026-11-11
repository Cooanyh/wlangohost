using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace PadP2P.App.Services;

internal sealed class LegacyPeerMonitor : IDisposable
{
    private const string OperationalLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    private const string WifiDirectAdapterPrefix = "Microsoft Wi-Fi Direct Virtual Adapter";
    private const int AssociationEventId = 20019;
    private const int MaximumPeers = 16;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private const int MissesBeforeRemoval = 3;

    private readonly object _sync = new();
    private readonly string _ssid;
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publishedAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private EventLogWatcher? _watcher;
    private Task? _probeTask;
    private bool _disposed;
    private string? _lastProbeErrorType;

    public LegacyPeerMonitor(string ssid) => _ssid = ssid;

    public event Action<IReadOnlyCollection<string>>? PeersChanged;

    public void Start()
    {
        try
        {
            var query = new EventLogQuery(
                OperationalLog,
                PathType.LogName,
                "*[System[(EventID=20019)]]");
            _watcher = new EventLogWatcher(query, bookmark: null, readExistingEvents: false);
            _watcher.EventRecordWritten += OnEventRecordWritten;
            _watcher.Enabled = true;
            _probeTask = Task.Run(() => ProbeLoopAsync(_cancellation.Token));
            BoundedAppLog.Info("wlan-peer-monitor.started");
        }
        catch (Exception exception)
        {
            _watcher?.Dispose();
            _watcher = null;
            BoundedAppLog.Warning("wlan-peer-monitor.unavailable", exception.GetType().Name);
        }
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null)
        {
            BoundedAppLog.Warning("wlan-peer-monitor.event-error", e.EventException.GetType().Name);
            return;
        }

        try
        {
            using var record = e.EventRecord;
            if (record is null || record.Id != AssociationEventId) return;

            var ns = XNamespace.Get("http://schemas.microsoft.com/win/2004/08/events/event");
            var eventData = XDocument.Parse(record.ToXml()).Root?.Element(ns + "EventData");
            if (eventData is null) return;

            var fields = eventData.Elements(ns + "Data")
                .Where(element => element.Attribute("Name") is not null)
                .ToDictionary(
                    element => (string)element.Attribute("Name")!,
                    element => element.Value,
                    StringComparer.OrdinalIgnoreCase);

            if (!fields.TryGetValue("SSID", out var eventSsid) ||
                !string.Equals(eventSsid, _ssid, StringComparison.Ordinal) ||
                !fields.TryGetValue("InterfaceDescription", out var description) ||
                !description.StartsWith(WifiDirectAdapterPrefix, StringComparison.OrdinalIgnoreCase) ||
                !fields.TryGetValue("PeerMAC", out var peerMac))
                return;

            var normalizedMac = NormalizeMac(peerMac);
            if (normalizedMac.Length == 0) return;

            lock (_sync)
            {
                if (_disposed) return;
                if (!_peers.ContainsKey(normalizedMac) && _peers.Count >= MaximumPeers)
                {
                    BoundedAppLog.Warning("wlan-peer-monitor.peer-limit-reached", $"limit={MaximumPeers}");
                    return;
                }

                if (!_peers.ContainsKey(normalizedMac))
                    _peers.Add(normalizedMac, new Peer(DateTimeOffset.UtcNow));
            }

            BoundedAppLog.Info("wlan-peer-monitor.association-received");
            SignalProbe();
        }
        catch (Exception exception)
        {
            BoundedAppLog.Warning("wlan-peer-monitor.event-parse-error", exception.GetType().Name);
        }
    }

    private async Task ProbeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _probeSignal.WaitAsync(ProbeInterval, cancellationToken).ConfigureAwait(false);
                await ProbePeersAsync(cancellationToken).ConfigureAwait(false);
                _lastProbeErrorType = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var errorType = exception.GetType().Name;
                if (!string.Equals(_lastProbeErrorType, errorType, StringComparison.Ordinal))
                {
                    _lastProbeErrorType = errorType;
                    var errorCode = exception is NetworkInformationException networkException
                        ? networkException.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : $"0x{exception.HResult:X8}";
                    BoundedAppLog.Warning("wlan-peer-monitor.probe-error", $"{errorType}; code={errorCode}");
                }
            }
        }
    }

    private async Task ProbePeersAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, Peer>[] peers;
        lock (_sync)
        {
            if (_disposed || _peers.Count == 0) return;
            peers = _peers.ToArray();
        }

        var addresses = IpNeighborTable.GetWifiDirectPeerAddresses();
        var results = await Task.WhenAll(peers.Select(async entry =>
        {
            if (!addresses.TryGetValue(entry.Key, out var address))
                return (entry.Key, Address: (IPAddress?)null, Reachable: false);

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(address, (int)ProbeTimeout.TotalMilliseconds).ConfigureAwait(false);
                return (entry.Key, Address: (IPAddress?)address, Reachable: reply.Status == IPStatus.Success);
            }
            catch (Exception exception) when (exception is PingException or InvalidOperationException or ArgumentException)
            {
                return (entry.Key, Address: (IPAddress?)address, Reachable: false);
            }
        })).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        string[]? changedSnapshot = null;
        lock (_sync)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;

            foreach (var result in results)
            {
                if (!_peers.TryGetValue(result.Key, out var peer)) continue;
                if (result.Reachable && result.Address is not null)
                {
                    peer.LastAddress = result.Address;
                    peer.ConsecutiveMisses = 0;
                }
                else
                {
                    peer.ConsecutiveMisses++;
                    if (peer.ConsecutiveMisses >= MissesBeforeRemoval && now - peer.FirstSeen >= StaleAfter)
                        _peers.Remove(result.Key);
                }
            }

            var currentAddresses = _peers.Values
                .Where(peer => peer.LastAddress is not null && peer.ConsecutiveMisses < MissesBeforeRemoval)
                .Select(peer => peer.LastAddress!.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_publishedAddresses.SetEquals(currentAddresses)) return;
            _publishedAddresses.Clear();
            _publishedAddresses.UnionWith(currentAddresses);
            changedSnapshot = _publishedAddresses.OrderBy(address => address, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (changedSnapshot is null) return;
        BoundedAppLog.Info("connected-device-count.changed", $"count={changedSnapshot.Length}");
        PeersChanged?.Invoke(changedSnapshot);
    }

    private void SignalProbe()
    {
        try
        {
            if (_probeSignal.CurrentCount == 0) _probeSignal.Release();
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        string[] cleared;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cleared = _publishedAddresses.ToArray();
            _publishedAddresses.Clear();
            _peers.Clear();
        }

        _cancellation.Cancel();
        try { _watcher?.Dispose(); }
        catch (EventLogException) { }
        _watcher = null;
        SignalProbe();
        if (_probeTask is not null)
        {
            try { _probeTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
        }
        _probeSignal.Dispose();
        _cancellation.Dispose();

        if (cleared.Length > 0) PeersChanged?.Invoke(Array.Empty<string>());
    }

    private static string NormalizeMac(string value)
    {
        Span<char> buffer = stackalloc char[12];
        var length = 0;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) continue;
            if (length == buffer.Length) return string.Empty;
            buffer[length++] = char.ToUpperInvariant(character);
        }
        return length == buffer.Length ? new string(buffer) : string.Empty;
    }

    private sealed class Peer(DateTimeOffset firstSeen)
    {
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public IPAddress? LastAddress { get; set; }
        public int ConsecutiveMisses { get; set; }
    }

    private static class IpNeighborTable
    {
        private const int AddressFamilyIpv4 = 2;
        private const string WifiDirectAdapterPrefix = "Microsoft Wi-Fi Direct Virtual Adapter";

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SockaddrInet
        {
            public ushort Family;
            public ushort Port;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] Data;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct MibIpNetRow2
        {
            public SockaddrInet Address;
            public uint InterfaceIndex;
            public ulong InterfaceLuid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
            public uint PhysicalAddressLength;
            public int State;
            public byte Flags;
            public uint ReachabilityTime;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct MibIpNetTableHeader
        {
            public uint NumEntries;
            public MibIpNetRow2 FirstRow;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIpNetTable2(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr table);

        public static Dictionary<string, IPAddress> GetWifiDirectPeerAddresses()
        {
            var indices = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    IsWifiDirectAdapter(adapter.Description))
                .Select(adapter => adapter.GetIPProperties().GetIPv4Properties()?.Index)
                .Where(index => index.HasValue)
                .Select(index => (uint)index!.Value)
                .ToHashSet();

            var results = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            if (indices.Count == 0) return results;

            var error = GetIpNetTable2(0, out var table);
            if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);

            try
            {
                var count = Marshal.ReadInt32(table);
                var firstRowOffset = (int)Marshal.OffsetOf<MibIpNetTableHeader>(nameof(MibIpNetTableHeader.FirstRow));
                var rowSize = Marshal.SizeOf<MibIpNetRow2>();
                for (var index = 0; index < count; index++)
                {
                    var rowPointer = IntPtr.Add(table, firstRowOffset + index * rowSize);
                    var row = Marshal.PtrToStructure<MibIpNetRow2>(rowPointer);
                    if (row.Address.Family != AddressFamilyIpv4 ||
                        !indices.Contains(row.InterfaceIndex) ||
                        row.PhysicalAddressLength < 6 ||
                        row.PhysicalAddress is null)
                        continue;

                    var mac = Convert.ToHexString(row.PhysicalAddress.AsSpan(0, 6));
                    var address = new IPAddress(row.Address.Data.AsSpan(0, 4));
                    if (address.Equals(IPAddress.Any) || address.GetAddressBytes()[0] >= 224) continue;
                    results.TryAdd(mac, address);
                }
            }
            finally
            {
                FreeMibTable(table);
            }

            return results;
        }

        private static bool IsWifiDirectAdapter(string description)
        {
            if (string.Equals(description, WifiDirectAdapterPrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            var numberedPrefix = WifiDirectAdapterPrefix + " #";
            return description.StartsWith(numberedPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(description.AsSpan(numberedPrefix.Length), out _);
        }
    }
}
