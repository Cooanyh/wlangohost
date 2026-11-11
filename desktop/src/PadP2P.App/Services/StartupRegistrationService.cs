using System.IO;
using Microsoft.Win32;

namespace PadP2P.App.Services;

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WlanGO";
    private const string LegacyValueName = "PadP2P";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the application executable.");
            key.SetValue(ValueName, $"\"{executable}\" --start-hidden", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void EnsureEnabled(bool enabled)
    {
        if (!enabled)
        {
            SetEnabled(false);
            return;
        }

        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate the application executable.");
        var (valueName, command) = ReadRegistration();
        var registeredExecutable = ExtractExecutablePath(command);
        if (registeredExecutable is null || !File.Exists(registeredExecutable))
        {
            SetEnabled(true);
            return;
        }

        var currentDirectory = Path.GetDirectoryName(Path.GetFullPath(currentExecutable));
        var registeredDirectory = Path.GetDirectoryName(Path.GetFullPath(registeredExecutable));
        if (!string.Equals(currentDirectory, registeredDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (valueName == LegacyValueName)
                PreserveRegistrationAsCurrentName(command!);
            return;
        }

        // If the app was upgraded in place, point startup at the current executable.
        SetEnabled(true);
    }

    public bool IsRegisteredForCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var executable = Environment.ProcessPath;
        return value is not null && executable is not null &&
               value.StartsWith($"\"{executable}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? ValueName, string? Command) ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(ValueName) as string;
        if (!string.IsNullOrWhiteSpace(current))
            return (ValueName, current);
        var legacy = key?.GetValue(LegacyValueName) as string;
        return string.IsNullOrWhiteSpace(legacy) ? (null, null) : (LegacyValueName, legacy);
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string? executable = null;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1)
                executable = expanded[1..closingQuote];
        }
        else
        {
            var extensionEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (extensionEnd >= 0)
                executable = expanded[..(extensionEnd + 4)].Trim();
        }

        if (string.IsNullOrWhiteSpace(executable)) return null;
        try { return Path.GetFullPath(executable); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static void PreserveRegistrationAsCurrentName(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }
}
