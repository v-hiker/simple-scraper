using System.Text.RegularExpressions;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public static class MetadataLinks
{
    public static Uri? Get(string provider, string id, LibraryMediaKind kind)
    {
        if (provider.Equals("imdb", StringComparison.OrdinalIgnoreCase)) return Regex.IsMatch(id, @"^tt\d+$") ? new("https://www.imdb.com/title/" + id + "/") : null;
        if (!long.TryParse(id, out var number) || number <= 0 || id.Any(c => !char.IsAsciiDigit(c))) return null;
        return provider.ToLowerInvariant() switch
        {
            "tmdb" => new("https://www.themoviedb.org/" + (kind == LibraryMediaKind.Movie ? "movie/" : "tv/") + id),
            "bangumi" => new("https://bgm.tv/subject/" + id),
            "tvdb" => new("https://www.thetvdb.com/dereferrer/" + (kind == LibraryMediaKind.Movie ? "movie/" : "series/") + id),
            _ => null
        };
    }
}
