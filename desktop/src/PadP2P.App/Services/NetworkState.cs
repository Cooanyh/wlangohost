namespace PadP2P.App.Services;

internal enum NetworkState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

internal sealed record NetworkStateChangedEventArgs(NetworkState State, int ConnectedCount);
internal sealed record PeerChangedEventArgs(bool Connected, string DeviceInfo);
