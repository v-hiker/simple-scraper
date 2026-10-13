using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleScraper.Services;

public sealed class SynopsisTranslationService(HttpClient? client = null, string? cacheDirectory = null, ITranslationProvider? provider = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITranslationProvider _provider = provider ?? new MyMemoryTranslationProvider("", client ?? TranslationProviders.Http);
    public string ProviderName => L.Text(_provider.Name);
    private readonly string _directory = cacheDirectory ?? Path.Combine(AppDataPaths.Root, "cache", "translations");
    private sealed record CacheEntry(string Provider, string Target, string SourceHash, string Text);
    public static string Target(string language) => language.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) || language.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : language;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string CachePath(string text, string target) => Path.Combine(_directory, Hash(_provider.CacheIdentity + "\n" + target.ToLowerInvariant() + "\n" + text) + ".json");
    public string? ReadCache(string text, string target)
    {
        target = Target(target);
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(CachePath(text, target)));
            return entry != null && entry.Provider == _provider.CacheIdentity && string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase) && entry.SourceHash == Hash(text) && !string.IsNullOrWhiteSpace(entry.Text) ? entry.Text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task<string> TranslateAsync(string text, string target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(L.Text("No synopsis to translate."));
        target = Target(target);
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, @"^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$")) throw new ArgumentException(L.Text("Invalid translation language."));
        var path = CachePath(text, target); var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (ReadCache(text, target) is { } cached) return cached;
            _provider.Validate();
            var result = new StringBuilder();
            foreach (var paragraph in System.Text.RegularExpressions.Regex.Split(text, @"(\r\n|\n|\r)"))
            {
                if (string.IsNullOrWhiteSpace(paragraph)) { result.Append(paragraph); continue; }
                var first = true;
                foreach (var chunk in Chunks(paragraph, _provider.MaxUtf8Bytes))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var translated = await _provider.TranslateAsync(chunk, target, cancellationToken);
                    if (string.IsNullOrWhiteSpace(translated)) throw new InvalidDataException(L.Text("Translation request failed. Try again later."));
                    if (translated == chunk) { result.Append(chunk); first = false; continue; }
                    if (!first && !target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !target.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) result.Append(' ');
                    result.Append(translated); first = false;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var output = result.ToString();
            if (output == text) return output;
            Directory.CreateDirectory(_directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CacheEntry(_provider.CacheIdentity, target, Hash(text), output)), new UTF8Encoding(false), cancellationToken); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return output;
        }
        finally { gate.Release(); }
    }
    public static IReadOnlyList<string> Chunks(string text, int maxUtf8Bytes = 480)
    {
        if (maxUtf8Bytes < 4) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        var result = new List<string>(); var chunk = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxUtf8Bytes) { result.Add(chunk.ToString()); chunk.Clear(); bytes = 0; }
            chunk.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
            if (bytes >= maxUtf8Bytes / 2 && rune.Value is '.' or '!' or '?' or '。' or '！' or '？' or ' ') { result.Add(chunk.ToString()); chunk.Clear(); bytes = 0; }
        }
        if (chunk.Length > 0) result.Add(chunk.ToString());
        return result;
    }
    public static string KnownLanguage(string text)
    {
        var letters = text.Count(char.IsLetter);
        if (letters == 0) return "";
        // A quoted foreign title inside a Chinese synopsis is not its language.
        if (text.Count(c => c is >= '\u3040' and <= '\u30ff') >= letters * .25) return "ja";
        if (text.Count(c => c is >= '\uac00' and <= '\ud7af') >= letters * .25) return "ko";
        // Han-only text is ambiguous; leave it to the service's detection.
        return "";
    }
}
