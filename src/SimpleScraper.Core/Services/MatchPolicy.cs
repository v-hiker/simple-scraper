using System.Globalization;
using System.Text;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public sealed class NeedsReviewException(string message) : Exception(message);

/// <summary>Conservative auto-match: exact normalized title, consistent year, one candidate.</summary>
public static class MatchPolicy
{
    public static MovieMetadata? Movie(string title, int year, IEnumerable<MovieMetadata> candidates)
    {
        var exact = candidates.Where(c => Equal(title, c.Title) || Equal(title, c.OriginalTitle)).ToList();
        if(year > 0)
        {
            var sameYear = exact.Where(c => c.Year == year).ToList();
            if(sameYear.Count > 0) exact = sameYear;
            else exact = exact.Where(c => Math.Abs(c.Year - year) <= 1).ToList();
        }
        return exact.Count == 1 ? exact[0] : null;
    }
    public static int? Tv(string title, IEnumerable<(int TmdbId, string Name, int Year, string? PosterPath, string Overview)> candidates)
    {
        var exact = candidates.Where(c => Equal(title,c.Name)).ToList();
        return exact.Count == 1 ? exact[0].TmdbId : null;
    }
    private static bool Equal(string? left, string? right) => !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right) && Normalize(left) == Normalize(right);
    private static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
