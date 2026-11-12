using Microsoft.Win32;

namespace PadP2P.App.Services;

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WlanGO";
    private const string LegacyValueName = "PadP2P";
    private readonly string _runKeyPath;
    private readonly Func<string?> _executablePathProvider;

    public StartupRegistrationService()
        : this(RunKeyPath, () => Environment.ProcessPath)
    {
    }

    internal StartupRegistrationService(string runKeyPath, Func<string?> executablePathProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        ArgumentNullException.ThrowIfNull(executablePathProvider);
        _runKeyPath = runKeyPath;
        _executablePathProvider = executablePathProvider;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled)
        {
            var executable = _executablePathProvider() ?? throw new InvalidOperationException("Could not locate the application executable.");
            key.SetValue(ValueName, $"\"{executable}\" --start-hidden", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void EnsureEnabled(bool enabled)
    {
        // Re-register from the current executable so moves and custom install
        // directories cannot leave Windows pointing at an obsolete copy.
        SetEnabled(enabled);
    }

    public bool IsRegisteredForCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var executable = _executablePathProvider();
        return value is not null && executable is not null &&
               value.StartsWith($"\"{executable}\"", StringComparison.OrdinalIgnoreCase);
    }
}
