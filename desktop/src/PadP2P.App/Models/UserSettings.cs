namespace PadP2P.App.Models;

public sealed class UserSettings
{
    public string Ssid { get; set; } = "WlanGO-Direct";
    public string Passphrase { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }
    public bool AutoStartNetwork { get; set; }
    public bool ShowLaunchers { get; set; } = true;
    public string MultipleLaunchMode { get; set; } = "Select";
    public List<LauncherEntry> Launchers { get; set; } = [];
    public string Language { get; set; } = "System";
    public string LastNetworkState { get; set; } = "Stopped";
}
