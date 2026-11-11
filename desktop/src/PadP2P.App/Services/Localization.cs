using System.Globalization;
using System.Windows;

namespace PadP2P.App.Services;

internal static class UiText
{
    private const string DictionaryPrefix = "/WlanGO;component/Resources/Strings.";
    private static string _language = string.Empty;

    public static event EventHandler? LanguageChanged;

    public static string Language
    {
        get => _language;
        set
        {
            var normalized = value is "zh-CN" or "en-US" ? value : "System";
            if (_language == normalized) return;
            _language = normalized;
            ApplyDictionary();
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    private static void ApplyDictionary()
    {
        var app = Application.Current;
        if (app is null) return;

        var selected = _language == "System"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"
            : _language;
        var merged = app.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.Contains("/Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true)
            {
                index = i;
                break;
            }
        }

        var uri = new Uri($"{DictionaryPrefix}{selected}.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = uri };
        if (index >= 0) merged[index] = dictionary;
        else merged.Add(dictionary);
    }
}
