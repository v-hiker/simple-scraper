using SimpleScraper.Models;
using SimpleScraper.Services;

// The UI algorithm before the performance change, captured for comparison.
// Keep this reference separate from the optimized production projection.
internal sealed record BaselineRow(LibraryMedia Media, int? Season, LocalMediaFile? File, int Depth, bool? Expanded, IReadOnlyList<string> Values)
{
    public string Key => File?.Path ?? Media.Folder + (Season is int s ? "|season:" + s : "");
}

internal static class BaselineRows
{
    public static List<BaselineRow> Build(IReadOnlyList<LibraryMedia> media, IReadOnlyList<LibraryColumnPreference> columns, IReadOnlySet<string> expanded, string query, int state, string sort, bool descending)
    {
        var filtered = media.Where(m => LibraryReview.MatchesQuery(m, query) && (state switch { 1 => LibraryReview.MatchStatus(m) != "Matched", 2 => m.NfoPath == null, 3 => m.PosterPath == null, 4 => LibraryReview.NeedsAttention(m), _ => true })).ToList();
        filtered.Sort((a, b) => (descending ? -1 : 1) * Compare(a, b, sort));
        var rows = new List<BaselineRow>();
        void Add(LibraryMedia m, int? season, LocalMediaFile? file, int depth, bool children = false)
        {
            var key = file?.Path ?? m.Folder + (season is int s ? "|season:" + s : "");
            rows.Add(new(m, season, file, depth, children ? expanded.Contains(key) : null, columns.Select(c => Value(m, season, file, c.Id)).ToList()));
        }
        foreach (var m in filtered)
        {
            Add(m, null, null, 0, m.Kind == LibraryMediaKind.Series);
            if (m.Kind != LibraryMediaKind.Series || !expanded.Contains(m.Folder)) continue;
            foreach (var group in m.Files.GroupBy(f => LibraryReview.IsExtra(m, f) ? -3 : f.NeedsReview ? -2 : f.Exclusion.Length > 0 ? -1 : f.Season).OrderBy(g => g.Key < 0 ? 10000 + g.Key : g.Key))
            {
                Add(m, group.Key, null, 1, true);
                if (expanded.Contains(m.Folder + "|season:" + group.Key)) foreach (var f in group.OrderBy(f => f.Episode).ThenBy(f => f.Name)) Add(m, group.Key, f, 2);
            }
        }
        return rows;
    }

    private static int Compare(LibraryMedia a, LibraryMedia b, string sort)
    {
        double Number(LibraryMedia m) => sort switch { "year" => m.Metadata?.Year > 0 ? m.Metadata.Year : m.Year, "rating" => m.Metadata?.Rating ?? 0, "seasons" => m.Files.Where(f => f.Exclusion.Length == 0).Select(f => f.Season).Distinct().Count(), "episodes" => m.Files.Count(f => f.Exclusion.Length == 0), "excluded" => m.Files.Count(f => f.Exclusion.Length > 0), _ => 0 };
        return sort is "year" or "rating" or "seasons" or "episodes" or "excluded" ? Number(a).CompareTo(Number(b)) : StringComparer.CurrentCultureIgnoreCase.Compare(Value(a, null, null, sort), Value(b, null, null, sort));
    }

    private static string Value(LibraryMedia m, int? season, LocalMediaFile? f, string column)
    {
        var resolved = f == null ? null : EpisodeMatching.Resolve(m, f);
        var doc = f != null ? MetadataEditing.Episode(m, f) : season is >= 0 ? Season(m, season.Value) : m.Metadata;
        var extra = f != null && LibraryReview.IsExtra(m, f);
        if (column == "title") return f != null ? (f.Exclusion.Length > 0 || extra ? f.Name : $"{f.Episode}. {doc?.Title ?? f.Name}") : season is int s ? s == -3 ? L.Text("Extra videos") : s == -2 ? L.Text("Needs episode matching / SP") : s < 0 ? L.Text("Excluded files") : m.Edits.Seasons.ContainsKey(s) ? $"S{s:00} · {doc?.Title}" : s == 0 ? L.Text("Specials / SP") : L.Format($"Season {s}") : doc?.Title ?? m.Title;
        return column switch
        {
            "year" => (doc?.Year > 0 ? doc.Year : m.Year).ToString(), "type" => L.Text(f != null ? f.Extra != null && resolved == null ? "Extra video" : "Episode" : season != null ? "Season" : m.Kind == LibraryMediaKind.Series ? "TV" : "Movie"),
            "match" => f != null ? L.Text(extra ? "Extra video" : f.NeedsReview ? "Needs manual matching" : f.Exclusion.Length > 0 ? "Excluded" : resolved == null ? "Not matched" : "Matched") : L.Text(season == -3 ? "Extra video" : season == -2 ? "Needs manual matching" : season == -1 ? "Excluded" : LibraryReview.MatchStatus(m, season)),
            "nfo" => f != null ? f.NfoPath == null ? "—" : "✓" : season is >= 0 ? m.SeasonNfoPaths.ContainsKey(season.Value) ? "✓" : "—" : m.ScanWarning.Length > 0 ? L.Text("Needs attention") : m.NfoPath == null ? "—" : "✓", "poster" => (f != null ? f.ThumbPath : season is >= 0 ? m.SeasonArtworkPaths.GetValueOrDefault(season.Value)?.GetValueOrDefault("poster") : m.PosterPath) == null ? "—" : "✓",
            "original" => doc?.OriginalTitle ?? "", "rating" => doc?.Rating > 0 ? doc.Rating.ToString("0.0") : "—", "source" => extra ? "—" : resolved?.Document.Provider ?? string.Join(" / ", m.Matches.Values.Select(v => v.Candidate.Provider).Distinct()), "seasons" => f != null ? extra ? "—" : f.Season.ToString() : season != null ? season.Value < 0 ? "—" : season.Value.ToString() : m.Files.Where(v => v.Exclusion.Length == 0).Select(v => v.Season).Distinct().Count().ToString(), "episodes" => f != null ? extra ? "—" : f.NeedsReview ? f.SourceNumber : f.Episode.ToString() : m.Files.Count(v => season == -3 ? LibraryReview.IsExtra(m, v) : season == -2 ? v.NeedsReview : season < 0 ? v.Exclusion.Length > 0 && !v.NeedsReview && !LibraryReview.IsExtra(m, v) : v.Exclusion.Length == 0 && (season == null || v.Season == season)).ToString(), "excluded" => m.Files.Count(v => v.Exclusion.Length > 0 && !v.NeedsReview).ToString(), "ids" => string.Join(" · ", (doc?.Ids ?? m.ExistingIds).Select(p => p.Key + " " + p.Value)), "path" => f?.Path ?? m.Folder, "filename" => f?.Name ?? Path.GetFileName(m.Folder), _ => ""
        };
    }

    // Captured separately because the production default Season API also gains
    // an index. The old UI baseline must retain its repeated full-list lookup.
    public static MetadataDocument Season(LibraryMedia media, int number)
    {
        if (media.Edits.Seasons.TryGetValue(number, out var edited)) return edited;
        var match = media.Matches.GetValueOrDefault(number)?.Document;
        var resolved = media.Files.Where(f => f.Season == number).Select(f => EpisodeMatching.Resolve(media, f)).OfType<EpisodeChoice>().ToList();
        var source = match ?? resolved.FirstOrDefault()?.Document;
        var sourceNumbers = resolved.Where(r => r.Document.Provider == source?.Provider && r.Document.Id == source?.Id).Select(r => r.Episode.Season).Distinct().ToList();
        var remoteNumber = sourceNumbers.Count == 1 ? sourceNumbers[0] : number;
        var season = source?.Seasons.FirstOrDefault(s => s.Number == remoteNumber);
        if (season == null && media.LocalSeasons.TryGetValue(number, out var local)) return local;
        var document = new MetadataDocument { Title = season?.Title ?? (number == 0 ? L.Text("Specials / SP") : L.Format($"Season {number}")), Overview = season?.Overview ?? "", Date = season?.Date ?? "", PosterUrl = season?.PosterUrl ?? "", Rating = season?.Rating ?? 0, CreditsLoaded = true };
        if (source?.Provider == "Bangumi" && number > 0)
            document = new() { Provider = source.Provider, Id = source.Id, Ids = source.Ids, Title = season?.Title ?? source.Title, OriginalTitle = source.OriginalTitle, Overview = season?.Overview ?? source.Overview, OriginalOverview = source.OriginalOverview, OverviewLanguage = source.OverviewLanguage, Date = season?.Date ?? source.Date, PosterUrl = season?.PosterUrl ?? source.PosterUrl, Rating = source.Rating, VoteCount = source.VoteCount, Cast = source.Cast, Crew = source.Crew, Studios = source.Studios, Genres = source.Genres, CreditsLoaded = source.CreditsLoaded };
        return document;
    }
}
