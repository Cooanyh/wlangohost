using System.IO;
using System.Security;
using System.Text;

namespace PadP2P.App.Services;

internal static class BoundedAppLog
{
    private const int MaxFileBytes = 64 * 1024;
    private const int ArchiveCount = 2;
    private const int MaxDetailLength = 512;
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WlanGO",
        "logs",
        "desktop.log");

    public static void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    public static void Warning(string eventName, string? detail = null) => Write("WARN", eventName, detail);

    public static void Error(string eventName, Exception exception) =>
        Write("ERROR", eventName, $"{exception.GetType().Name}; HRESULT=0x{exception.HResult:X8}");

    private static void Write(string level, string eventName, string? detail)
    {
        var safeEvent = Sanitize(eventName, 96);
        var safeDetail = Sanitize(detail ?? string.Empty, MaxDetailLength);
        var line = $"{DateTimeOffset.UtcNow:O}\t{level}\t{safeEvent}\t{safeDetail}{Environment.NewLine}";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > MaxFileBytes)
            bytes = Encoding.UTF8.GetBytes(line[..Math.Min(line.Length, MaxFileBytes / 2)]);

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length + bytes.Length > MaxFileBytes)
                    Rotate();

                using var stream = new FileStream(
                    LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length + bytes.Length > MaxFileBytes)
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                }
                stream.Write(bytes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }

    private static void Rotate()
    {
        for (var index = ArchiveCount; index >= 2; index--)
        {
            var source = $"{LogPath}.{index - 1}";
            var destination = $"{LogPath}.{index}";
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }

        if (File.Exists(LogPath)) File.Move(LogPath, $"{LogPath}.1", overwrite: true);
    }

    private static string Sanitize(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
