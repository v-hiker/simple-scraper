using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public interface ITranslationProvider
{
    string Name { get; }
    string CacheIdentity { get; }
    int MaxUtf8Bytes { get; }
    void Validate();
    Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken);
}

public sealed record TranslationProviderOption(string Id, string Name, bool NeedsEndpoint = false, bool NeedsKey = false, bool HasEmail = false);

public static class TranslationProviders
{
    public static IReadOnlyList<TranslationProviderOption> Options { get; } = new[]
    {
        new TranslationProviderOption("MyMemory", "MyMemory", HasEmail: true),
        new TranslationProviderOption("GoogleTranslate", "Google Translate (no key)"),
        new TranslationProviderOption("GoogleCloud", "Google Cloud", NeedsKey: true),
        new TranslationProviderOption("LibreTranslate", "LibreTranslate", NeedsEndpoint: true, NeedsKey: true),
    };
    internal static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public static ITranslationProvider Create(TranslationSettings settings, HttpClient? client = null) => settings.Provider switch
    {
        "MyMemory" => new MyMemoryTranslationProvider(settings.MyMemoryEmail, client ?? Http),
        "GoogleTranslate" => new GoogleWebTranslationProvider(client ?? Http),
        "GoogleCloud" => new GoogleCloudTranslationProvider(settings.GoogleApiKey, client ?? Http),
        "LibreTranslate" => new LibreTranslationProvider(settings.LibreEndpoint, settings.LibreApiKey, client ?? Http),
        _ => throw new InvalidOperationException(L.Text("Unknown translation source.")),
    };
    internal static void EnsureSuccess(HttpResponseMessage response, bool needsKey = false)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException(L.Text("Translation limit reached. Try again later."));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidOperationException(L.Text(needsKey ? "Translation access denied. Check the API key and service settings." : "Translation service unavailable. Try another source."));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(L.Format($"Translation request failed (HTTP {(int)response.StatusCode})."));
    }
    internal static string RequiredText(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? WebUtility.HtmlDecode(value.GetString()!).Trim() : throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
}

public sealed class MyMemoryTranslationProvider(string email, HttpClient http) : ITranslationProvider
{
    public string Name => "MyMemory";
    public string CacheIdentity => "MyMemory:v1";
    public int MaxUtf8Bytes => 480;
    public void Validate()
    {
        if (email.Length > 0 && (!MailAddress.TryCreate(email, out var address) || address.Address != email || !address.Host.Contains('.'))) throw new InvalidOperationException(L.Text("Enter a valid email address."));
    }
    public async Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken)
    {
        var uri = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString("autodetect|" + target);
        if (email.Length > 0) uri += "&de=" + Uri.EscapeDataString(email);
        using var response = await http.GetAsync(uri, cancellationToken);
        TranslationProviders.EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
        var code = ResponseCode(root);
        if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True || code == 429) throw new InvalidOperationException(L.Text("Translation limit reached. Try again later."));
        var details = root.TryGetProperty("responseDetails", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()?.Trim() : "";
        if (code == 403 && string.Equals(details, "PLEASE SELECT TWO DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase)) return text;
        if (code != 200 || !root.TryGetProperty("responseData", out var data)) throw new InvalidDataException(code == 0 ? L.Text("Translation request failed. Try again later.") : L.Format($"Translation request failed (service {code})."));
        return TranslationProviders.RequiredText(data, "translatedText");
    }
    private static int ResponseCode(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out var status)) return 0;
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code)) return code;
        return status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out code) ? code : 0;
    }
}

public sealed class GoogleCloudTranslationProvider(string key, HttpClient http) : ITranslationProvider
{
    public string Name => "Google Cloud";
    public string CacheIdentity => "GoogleCloud:v1";
    public int MaxUtf8Bytes => 12000;
    public void Validate() { if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(L.Text("Enter the translation API key in Settings.")); }
    public async Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(key))
        {
            Content = JsonContent.Create(new { q = text, target = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? target : target.Split('-')[0], format = "text", model = "nmt" }),
        };
        using var response = await http.SendAsync(request, cancellationToken);
        TranslationProviders.EnsureSuccess(response, needsKey: true);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("translations", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
        return TranslationProviders.RequiredText(list[0], "translatedText");
    }
}

// The keyless web endpoint has no published developer contract. Keep its
// response parser and cache separate from the official Google Cloud API.
public sealed class GoogleWebTranslationProvider(HttpClient http) : ITranslationProvider
{
    public string Name => "Google Translate (no key)";
    public string CacheIdentity => "GoogleTranslateWeb:v1";
    public int MaxUtf8Bytes => 1200;
    public void Validate() { }
    public async Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken)
    {
        var uri = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? target : target.Split('-')[0]) + "&dt=t&q=" + Uri.EscapeDataString(text);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SimpleScraper/0.1.0");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException(L.Text("Google Translate is temporarily rate limited. Try later or choose another source."));
        TranslationProviders.EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
        var result = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0 || segment[0].ValueKind != JsonValueKind.String) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
            result.Append(segment[0].GetString());
        }
        if (string.IsNullOrWhiteSpace(result.ToString())) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
        return result.ToString();
    }
}

public sealed class LibreTranslationProvider(string endpoint, string key, HttpClient http) : ITranslationProvider
{
    public string Name => "LibreTranslate";
    public string CacheIdentity => "LibreTranslate:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Trim().TrimEnd('/'))));
    public int MaxUtf8Bytes => 480;
    public void Validate()
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidOperationException(L.Text("Enter a valid translation service URL."));
    }
    public async Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken)
    {
        var uri = endpoint.Trim().TrimEnd('/');
        if (!uri.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)) uri += "/translate";
        var body = new Dictionary<string, string> { ["q"] = text, ["source"] = "auto", ["target"] = target.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ? "zt" : target.Split('-')[0].ToLowerInvariant(), ["format"] = "text" };
        if (!string.IsNullOrWhiteSpace(key)) body["api_key"] = key;
        using var response = await http.PostAsJsonAsync(uri, body, cancellationToken);
        TranslationProviders.EnsureSuccess(response, needsKey: !string.IsNullOrWhiteSpace(key));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return TranslationProviders.RequiredText(json.RootElement, "translatedText");
    }
}
