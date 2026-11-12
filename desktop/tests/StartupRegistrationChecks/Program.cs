using Microsoft.Win32;
using PadP2P.App.Services;

var keyPath = $@"Software\WlanGO.Tests\StartupRegistration\{Guid.NewGuid():N}";
const string oldExecutable = @"D:\old-build\WlanGO.exe";
const string currentExecutable = @"E:\WlanGO\WlanGO.exe";

try
{
    using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                     ?? throw new InvalidOperationException("Could not create the temporary test registry key."))
    {
        key.SetValue("WlanGO", $"\"{oldExecutable}\" --start-hidden", RegistryValueKind.String);
        key.SetValue("PadP2P", $"\"{oldExecutable}\" --start-hidden", RegistryValueKind.String);
    }

    var service = new StartupRegistrationService(keyPath, () => currentExecutable);
    service.EnsureEnabled(enabled: true);

    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)
                     ?? throw new InvalidOperationException("The temporary test registry key disappeared."))
    {
        var command = key.GetValue("WlanGO") as string;
        var expected = $"\"{currentExecutable}\" --start-hidden";
        if (!string.Equals(command, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected startup command '{expected}', got '{command}'.");
        if (key.GetValue("PadP2P") is not null)
            throw new InvalidOperationException("The legacy PadP2P startup entry was not removed.");
        if (!service.IsRegisteredForCurrentExecutable())
            throw new InvalidOperationException("The current executable was not recognized as registered.");
    }

    service.EnsureEnabled(enabled: false);
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
    {
        if (key?.GetValue("WlanGO") is not null || key?.GetValue("PadP2P") is not null)
            throw new InvalidOperationException("Disabling startup left a registry entry behind.");
    }

    Console.WriteLine("PASS: stale install paths are replaced and disabling startup removes current and legacy entries.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
}
