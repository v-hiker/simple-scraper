using System.Text.RegularExpressions;
using System.Text.Json;

namespace SimpleScraper.Services;

public sealed record OverviewText(string Current, string Original)
{
    // Some sources include both versions in one summary. Recognize headings,
    // not words appearing inside the synopsis, and keep cached summaries intact.
    public static OverviewText Split(string? overview, string? original = null)
    {
        var text = (overview ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var heading = Regex.Match(text, @"(?im)^[ \t]*(?:\[(?:简介原文|簡介原文|原文|Original(?: synopsis| overview)?)\]|【(?:简介原文|簡介原文|原文)】)[ \t]*$", RegexOptions.CultureInvariant);
        var current = heading.Success ? text[..heading.Index].Trim() : text;
        var raw = !string.IsNullOrWhiteSpace(original) ? original.Trim() : heading.Success ? text[(heading.Index + heading.Length)..].Trim() : "";
        return new(current, raw);
    }

    public static string TmdbOriginal(JsonElement root, string requestedLanguage)
    {
        if (!root.TryGetProperty("original_language", out var language) || language.ValueKind != JsonValueKind.String) return "";
        var originalLanguage = language.GetString();
        if (root.TryGetProperty("translations", out var catalog) && catalog.TryGetProperty("translations", out var translations) && translations.ValueKind == JsonValueKind.Array)
            foreach (var translation in translations.EnumerateArray())
                if (translation.TryGetProperty("iso_639_1", out var code) && code.GetString() == originalLanguage && translation.TryGetProperty("data", out var data) && data.TryGetProperty("overview", out var plot) && plot.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(plot.GetString())) return plot.GetString()!;
        // Only the requested original-language response is known to be original.
        return requestedLanguage.Split('-')[0].Equals(originalLanguage, StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("overview", out var current) && current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
    }
}
