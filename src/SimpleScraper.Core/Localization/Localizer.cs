using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleScraper.Localization;

/// <summary>Embedded translation catalogs; UI culture never changes media filenames or NFO formats.</summary>
public static class Localizer
{
    private static Dictionary<string,string> _english = Load("en-US");
    private static Dictionary<string,string> _current = _english;
    public static string Language { get; private set; } = "en-US";
    public static void Initialize(string language)
    {
        Language = language == "system"
            ? (CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US")
            : language == "zh-CN" ? "zh-CN" : "en-US";
        _current = Load(Language);
    }
    public static string Key(string source) => "T" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    public static string ByKey(string key) => _current.GetValueOrDefault(key) ?? _english.GetValueOrDefault(key) ?? key;
    public static string Text(string source)
    {
        var key = Key(source);
        return _current.GetValueOrDefault(key) ?? source;
    }
    public static string Format(FormattableString text)
    {
        try { return string.Format(CultureInfo.GetCultureInfo(Language), Text(text.Format), text.GetArguments()); }
        catch (FormatException) { return text.ToString(CultureInfo.InvariantCulture); }
    }
    private static Dictionary<string,string> Load(string language)
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream($"SimpleScraper.Localization.{language}.json");
        return stream == null ? new() : JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? new();
    }
}

/// <summary>Indexer binding used by XAML. A language selection takes effect after restart.</summary>
public sealed class LocalizedStrings
{
    public string this[string key] => Localizer.ByKey(key);
}
