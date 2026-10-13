using SimpleScraper.Models;

namespace SimpleScraper.Services;

public static class LibraryReview
{
    public static bool IsExtra(LibraryMedia media, LocalMediaFile file) => file.Extra != null && EpisodeMatching.Resolve(media, file) == null && (!file.ManuallyExcluded || file.Episode == 0);

    public static string MatchStatus(LibraryMedia media, int? season = null)
    {
        if (media.Kind != LibraryMediaKind.Series) return media.Matches.Count > 0 ? "Matched" : "Not matched";
        var files = media.Files.Where(f => season == null || f.Season == season).Where(f => f.Exclusion.Length == 0 || f.NeedsReview).ToList();
        if (files.Count == 0) return (season == null ? media.Matches.Count > 0 : media.Matches.ContainsKey(season.Value)) ? "Matched" : "Not matched";
        var matched = files.Count(f => !f.NeedsReview && EpisodeMatching.Resolve(media, f) != null);
        return matched == files.Count ? "Matched" : matched > 0 ? "Partially matched" : files.Any(f => f.NeedsReview) ? "Needs manual matching" : "Not matched";
    }

    public static bool NeedsAttention(LibraryMedia media) => media.ScanWarning.Length > 0 || media.Files.Any(f => f.NeedsReview) ||
        MatchStatus(media) != "Matched" || media.Kind == LibraryMediaKind.Series && media.Files.Where(f => f.Exclusion.Length == 0).GroupBy(f => (f.Season, f.Episode)).Any(g => g.Count() > 1);

    public static bool MatchesQuery(LibraryMedia media, string query) =>
        (media.Title + " " + media.Metadata?.Title + " " + media.Metadata?.OriginalTitle + " " + media.Year + " " + media.Metadata?.Year + " " + media.Folder).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        media.Files.Any(f => f.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool DuplicateLibrary(IEnumerable<MediaLibrary> libraries, MediaLibrary candidate) => libraries.Any(l => LibrarySnapshotStore.Key(l) == LibrarySnapshotStore.Key(candidate) ||
        (candidate.AutoLayout || l.AutoLayout) && l.Kind == candidate.Kind &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(l.Path)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path)), StringComparison.OrdinalIgnoreCase));
}

public static class RenameReview
{
    public static IEnumerable<RenamePreviewGroup> Visible(IEnumerable<RenamePreviewGroup> groups, int state, string query) => groups.Where(g =>
        (state switch { 1 => g.State == RenamePreviewState.Changed, 2 => g.State == RenamePreviewState.Conflict, 3 => g.State is RenamePreviewState.Unchanged or RenamePreviewState.Excluded or RenamePreviewState.Unmatched, _ => true }) &&
        (g.File.Name + " " + g.Operations.FirstOrDefault(p => p.Source == g.File.Path)?.Destination).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    public static void SelectVisible(IEnumerable<RenamePreviewGroup> groups, int state, string query, bool selected)
    {
        foreach (var group in Visible(groups, state, query).Where(g => g.State == RenamePreviewState.Changed)) group.Selected = selected;
    }
}
