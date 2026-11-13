using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace PadP2P.App.Services;

internal static class IconThemeService
{
    private const int RuntimeIconFrameSize = 64;
    private static readonly ConcurrentDictionary<string, BitmapSource> ImageCache = new(StringComparer.Ordinal);

    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public static void ApplyTo(Window window, bool? isDark = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var theme = (isDark ?? IsDark) ? "Dark" : "Light";
        window.Icon = LoadImage($"PadP2P.{theme}.ico");
    }

    public static BitmapSource LoadTitleIcon(bool isDark)
        => LoadImage($"PadP2P.{(isDark ? "Dark" : "Light")}.png");

    public static BitmapSource LoadStateIcon(string state, bool isDark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var theme = isDark ? "Dark" : "Light";
        return LoadImage($"PadP2P.{state}.{theme}.ico");
    }

    private static BitmapSource LoadImage(string name)
        => ImageCache.GetOrAdd(name, static assetName =>
        {
            var uri = new Uri($"pack://application:,,,/Assets/{assetName}", UriKind.Absolute);
            if (assetName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var decoder = new IconBitmapDecoder(
                    uri,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var runtimeFrame = decoder.Frames
                    .OrderBy(frame => Math.Abs(Math.Max(frame.PixelWidth, frame.PixelHeight) - RuntimeIconFrameSize))
                    .ThenByDescending(frame => Math.Max(frame.PixelWidth, frame.PixelHeight))
                    .First();
                runtimeFrame.Freeze();
                return runtimeFrame;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        });
}
