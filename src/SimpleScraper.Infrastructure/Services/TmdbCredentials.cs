using System.Security.Cryptography;
using System.Text;

namespace SimpleScraper.Services;

public sealed class TmdbApiKeyMissingException() : InvalidOperationException(L.Text("TMDB API key is missing. Enter your own key in Settings."));

public static class TmdbCredentials
{
    public const string RegistrationUrl = "https://www.themoviedb.org/settings/api";
    public static bool NeedsConfiguration(IEnumerable<string> sources, string? key) => sources.Contains("TMDB", StringComparer.OrdinalIgnoreCase) && Normalize(key).Length == 0;
    // Fingerprint only: retire the old upstream credential from saved settings
    // without shipping that credential or clearing a user's own key.
    private const string RetiredKeyFingerprint = "06481EA96A9A379451DE534BA952D0D15762AA5A5A830A2EA53CC3844002F62F";
    public static string Normalize(string? key)
    {
        var value = key?.Trim() ?? "";
        return value.Length > 0 && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))) == RetiredKeyFingerprint ? "" : value;
    }
}
