using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using PadP2P.App.Models;

namespace PadP2P.App.Services;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WlanGO", "settings.json");
    private readonly string _legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PadP2P", "settings.json");

    public UserSettings Load()
    {
        var sourcePath = File.Exists(_settingsPath) ? _settingsPath : _legacySettingsPath;
        if (File.Exists(sourcePath))
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(sourcePath))
                ?? throw new InvalidDataException("The PadP2P settings file is empty or invalid.");
            if (string.IsNullOrWhiteSpace(stored.ProtectedPassphrase))
                throw new InvalidDataException("The saved Wi-Fi password is missing from the protected settings.");

            var encrypted = Convert.FromBase64String(stored.ProtectedPassphrase);
            var plain = DpapiProtector.Unprotect(encrypted);
            try
            {
                var settings = new UserSettings
                {
                    Ssid = string.IsNullOrWhiteSpace(stored.Ssid) ? "WlanGO-Direct" : stored.Ssid,
                    Passphrase = Encoding.UTF8.GetString(plain),
                    StartWithWindows = stored.StartWithWindows,
                    AutoStartNetwork = stored.AutoStartNetwork,
                    ShowLaunchers = stored.ShowLaunchers ?? stored.ShowSuperDisplay,
                    MultipleLaunchMode = NormalizeMultipleLaunchMode(stored.MultipleLaunchMode),
                    Launchers = NormalizeLaunchers(stored),
                    Language = NormalizeLanguage(stored.Language),
                    LastNetworkState = stored.LastNetworkState ?? "Stopped",
                    IgnoredUpdateVersion = stored.IgnoredUpdateVersion ?? string.Empty
                };
                if (!string.Equals(sourcePath, _settingsPath, StringComparison.OrdinalIgnoreCase))
                    Save(settings);
                return settings;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        var migrated = new UserSettings
        {
            Passphrase = CreateStrongPassphrase(),
            Launchers =
            [
                new LauncherEntry
                {
                    Id = LauncherEntry.SuperDisplayId,
                    Name = "SuperDisplay",
                    IsEnabled = true
                }
            ]
        };
        Save(migrated);
        return migrated;
    }

    public void Save(UserSettings settings)
    {
        if (!NetworkProfileValidator.TryValidate(settings.Ssid, settings.Passphrase, out var validationError))
            throw new InvalidDataException(validationError);

        var plain = Encoding.UTF8.GetBytes(settings.Passphrase);
        byte[] encrypted;
        try { encrypted = DpapiProtector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var stored = new StoredSettings
        {
            Ssid = settings.Ssid,
            ProtectedPassphrase = Convert.ToBase64String(encrypted),
            StartWithWindows = settings.StartWithWindows,
            AutoStartNetwork = settings.AutoStartNetwork,
            ShowLaunchers = settings.ShowLaunchers,
            MultipleLaunchMode = settings.MultipleLaunchMode,
            Launchers = settings.Launchers,
            ShowSuperDisplay = settings.ShowLaunchers,
            SuperDisplayPath = settings.Launchers.FirstOrDefault(entry => entry.IsSuperDisplay)?.ExecutablePath ?? string.Empty,
            Language = settings.Language,
            LastNetworkState = settings.LastNetworkState,
            IgnoredUpdateVersion = settings.IgnoredUpdateVersion
        };
        CryptographicOperations.ZeroMemory(encrypted);

        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, _settingsPath, true);
    }

    private static string NormalizeLanguage(string? value) => value is "zh-CN" or "en-US" ? value : "System";

    private static string NormalizeMultipleLaunchMode(string? value) => value is "AllEnabled" or "Select" ? value : "Select";

    private static List<LauncherEntry> NormalizeLaunchers(StoredSettings stored)
    {
        if (stored.Launchers is null)
        {
            return
            [
                new LauncherEntry
                {
                    Id = LauncherEntry.SuperDisplayId,
                    Name = "SuperDisplay",
                    ExecutablePath = stored.SuperDisplayPath ?? string.Empty,
                    IsEnabled = true
                }
            ];
        }

        var normalized = new List<LauncherEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in stored.Launchers)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.Id) || ids.Contains(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
                while (!ids.Add(entry.Id))
                    entry.Id = Guid.NewGuid().ToString("N");
            }
            else
            {
                ids.Add(entry.Id);
            }
            if (string.IsNullOrWhiteSpace(entry.Name))
                entry.Name = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
            normalized.Add(entry);
        }

        return normalized;
    }

    private static string CreateStrongPassphrase()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%&()*+,-./:;=?@[]^_{|}~";
        return string.Create(24, alphabet, static (span, chars) =>
        {
            for (var index = 0; index < span.Length; index++)
                span[index] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    private sealed class StoredSettings
    {
        public string Ssid { get; set; } = "WlanGO-Direct";
        public string ProtectedPassphrase { get; set; } = string.Empty;
        public bool StartWithWindows { get; set; }
        public bool AutoStartNetwork { get; set; }
        public bool ShowSuperDisplay { get; set; } = true;
        public string? SuperDisplayPath { get; set; }
        public bool? ShowLaunchers { get; set; }
        public string? MultipleLaunchMode { get; set; }
        public List<LauncherEntry>? Launchers { get; set; }
        public string? Language { get; set; } = "System";
        public string? LastNetworkState { get; set; }
        public string? IgnoredUpdateVersion { get; set; }
    }
}
