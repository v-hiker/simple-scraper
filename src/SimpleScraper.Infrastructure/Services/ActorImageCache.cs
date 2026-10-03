using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleScraper.Services;

public sealed record CachedPerson(string Provider, int Id, string Name, string ImageUrl, DateTimeOffset UpdatedAt);

public sealed class ActorImageCache(string? directory = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultDirectory => Path.Combine(AppDataPaths.Root, "cache", "people");
    public string DirectoryPath { get; } = Path.GetFullPath(directory ?? DefaultDirectory);
    private string Folder(string provider, int id, string name, string? profile)
    {
        var source = provider.Equals("TMDB", StringComparison.OrdinalIgnoreCase) ? "tmdb" : provider.Equals("Bangumi", StringComparison.OrdinalIgnoreCase) ? "bangumi" : "other-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.ToLowerInvariant())))[..16];
        var identity = id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(profile) ? name : profile)));
        return Path.Combine(DirectoryPath, source, identity);
    }
    public string ImagePath(string provider, int id, string name, string? profile) => Path.Combine(Folder(provider, id, name, profile), "portrait.jpg");
    public string? FindImage(string provider, int id, string name, string? profile)
    {
        var path = ImagePath(provider, id, name, profile); return File.Exists(path) ? path : null;
    }
    public static string RemoteUrl(string provider, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "";
        if (Uri.TryCreate(profile, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.AbsoluteUri;
        return provider.Equals("TMDB", StringComparison.OrdinalIgnoreCase) && profile.StartsWith('/') ? "https://image.tmdb.org/t/p/original" + profile : "";
    }
    public async Task<(bool Downloaded, string? LocalPath)> EnsureAsync(string provider, int id, string name, string? profile, ImageDownloadService images)
    {
        var path = ImagePath(provider, id, name, profile); var gate = Gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync();
        try
        {
            var folder = Path.GetDirectoryName(path)!; Directory.CreateDirectory(folder);
            var url = RemoteUrl(provider, profile);
            var record = new CachedPerson(provider, id, name, url, DateTimeOffset.UtcNow);
            var json = Path.Combine(folder, "person.json"); var temp = json + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(record), new UTF8Encoding(false)); File.Move(temp, json, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (File.Exists(path)) return (false, path);
            // Existing imported local portraits can also enter the shared app cache.
            if (url.Length == 0 && profile != null && Path.IsPathRooted(profile) && File.Exists(profile)) { File.Copy(profile, path, false); return (false, path); }
            if (url.Length == 0) return (false, null);
            return await images.DownloadAnyImageAsync(url, path) ? (true, path) : (false, null);
        }
        finally { gate.Release(); }
    }
}
