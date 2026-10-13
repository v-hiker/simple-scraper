using SimpleScraper.Models;
using SimpleScraper.Services;

namespace SimpleScraper.Presentation;

public sealed record LibraryProjectedRow(LibraryMedia Media, int? Season, LocalMediaFile? File, int Depth, bool? Expanded, IReadOnlyList<string> Values)
{
    public string Key => File?.Path ?? Media.Folder + (Season is int season ? "|season:" + season : "");
}

/// <summary>Projects one immutable refresh snapshot without constructing XAML controls or reading media files.</summary>
public static class LibraryRowProjection
{
    public static List<LibraryProjectedRow> Build(IReadOnlyList<LibraryMedia> media, IReadOnlyList<LibraryColumnPreference> columns,
        IReadOnlySet<string> expanded, string query, int state, string sort, bool descending, CancellationToken cancellationToken = default)
    {
        var contexts = new List<MediaContext>();
        foreach (var item in media)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LibraryReview.MatchesQuery(item, query)) continue;
            var context = new MediaContext(item, cancellationToken);
            if (state switch { 1 => context.MatchStatus(null) != "Matched", 2 => item.NfoPath == null, 3 => item.PosterPath == null, 4 => context.NeedsAttention, _ => true })
                contexts.Add(context);
        }
        // Compute the sort value once per work, rather than during each comparison.
        if (sort is "year" or "rating" or "seasons" or "episodes" or "excluded")
        {
            var keys = contexts.ToDictionary(c => c, c => c.Number(sort));
            contexts.Sort((a, b) => (descending ? -1 : 1) * keys[a].CompareTo(keys[b]));
        }
        else
        {
            var keys = contexts.ToDictionary(c => c, c => c.Value(null, null, sort));
            contexts.Sort((a, b) => (descending ? -1 : 1) * StringComparer.CurrentCultureIgnoreCase.Compare(keys[a], keys[b]));
        }

        var rows = new List<LibraryProjectedRow>();
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = context.Media;
            void Add(int? season, LocalMediaFile? file, int depth, bool children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = file?.Path ?? item.Folder + (season is int number ? "|season:" + number : "");
                rows.Add(new(item, season, file, depth, children ? expanded.Contains(key) : null,
                    columns.Select(column => context.Value(season, file, column.Id)).ToArray()));
            }
            Add(null, null, 0, item.Kind == LibraryMediaKind.Series);
            if (item.Kind != LibraryMediaKind.Series || !expanded.Contains(item.Folder)) continue;
            foreach (var group in context.Groups.OrderBy(group => group.Key < 0 ? 10000 + group.Key : group.Key))
            {
                Add(group.Key, null, 1, true);
                if (expanded.Contains(item.Folder + "|season:" + group.Key))
                    foreach (var file in group.Value.OrderBy(file => file.Episode).ThenBy(file => file.Name)) Add(group.Key, file, 2, false);
            }
        }
        return rows;
    }

    private sealed class MatchCounts
    {
        public int Total, Matched;
        public bool NeedsReview;
        public void Add(LocalMediaFile file, EpisodeChoice? resolved)
        {
            if (file.Exclusion.Length > 0 && !file.NeedsReview) return;
            Total++;
            if (!file.NeedsReview && resolved != null) Matched++;
            NeedsReview |= file.NeedsReview;
        }
    }

    private sealed class MediaContext
    {
        public LibraryMedia Media { get; }
        public Dictionary<int, List<LocalMediaFile>> Groups { get; } = new();
        public bool NeedsAttention { get; }
        private readonly MetadataDocument? _metadata;
        private readonly string _sources;
        private readonly Dictionary<LocalMediaFile, EpisodeChoice?> _resolved = new();
        private readonly HashSet<LocalMediaFile> _extras = new();
        private readonly Dictionary<LocalMediaFile, MetadataDocument> _episodeDocuments = new();
        private readonly Dictionary<int, MetadataDocument> _seasonDocuments = new();
        private readonly Dictionary<int, List<EpisodeChoice>> _resolvedSeasons = new();
        private readonly Dictionary<int, MatchCounts> _seasonMatches = new();
        private readonly Dictionary<int, int> _includedPerSeason = new();
        private readonly MatchCounts _matches = new();
        private readonly int _included, _excluded, _excludedWithoutReview, _extraCount, _reviewCount, _excludedGroup;

        public MediaContext(LibraryMedia media, CancellationToken cancellationToken)
        {
            Media = media; _metadata = media.Metadata;
            _sources = string.Join(" / ", media.Matches.Values.Select(match => match.Candidate.Provider).Distinct());
            var resolver = new EpisodeResolutionIndex(media);
            var numbers = new HashSet<(int Season, int Episode)>();
            var duplicate = false;
            foreach (var file in media.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = resolver.Resolve(file); _resolved[file] = resolved;
                var extra = file.Extra != null && resolved == null && (!file.ManuallyExcluded || file.Episode == 0);
                if (extra) { _extras.Add(file); _extraCount++; }
                if (file.NeedsReview) _reviewCount++;
                if (file.Exclusion.Length == 0)
                {
                    _included++;
                    _includedPerSeason[file.Season] = _includedPerSeason.GetValueOrDefault(file.Season) + 1;
                    duplicate |= !numbers.Add((file.Season, file.Episode));
                }
                else
                {
                    _excluded++;
                    if (!file.NeedsReview) { _excludedWithoutReview++; if (!extra) _excludedGroup++; }
                }
                _matches.Add(file, resolved);
                if (!_seasonMatches.TryGetValue(file.Season, out var match)) _seasonMatches[file.Season] = match = new();
                match.Add(file, resolved);
                if (resolved != null)
                {
                    if (!_resolvedSeasons.TryGetValue(file.Season, out var choices)) _resolvedSeasons[file.Season] = choices = new();
                    choices.Add(resolved);
                }
                var group = extra ? -3 : file.NeedsReview ? -2 : file.Exclusion.Length > 0 ? -1 : file.Season;
                if (!Groups.TryGetValue(group, out var files)) Groups[group] = files = new();
                files.Add(file);
            }
            NeedsAttention = media.ScanWarning.Length > 0 || _reviewCount > 0 || MatchStatus(null) != "Matched" || media.Kind == LibraryMediaKind.Series && duplicate;
        }

        public string MatchStatus(int? season)
        {
            if (Media.Kind != LibraryMediaKind.Series) return Media.Matches.Count > 0 ? "Matched" : "Not matched";
            var counts = season == null ? _matches : _seasonMatches.GetValueOrDefault(season.Value);
            if (counts == null || counts.Total == 0) return (season == null ? Media.Matches.Count > 0 : Media.Matches.ContainsKey(season.Value)) ? "Matched" : "Not matched";
            return counts.Matched == counts.Total ? "Matched" : counts.Matched > 0 ? "Partially matched" : counts.NeedsReview ? "Needs manual matching" : "Not matched";
        }

        public double Number(string column) => column switch
        {
            "year" => _metadata?.Year > 0 ? _metadata.Year : Media.Year, "rating" => _metadata?.Rating ?? 0,
            "seasons" => _includedPerSeason.Count, "episodes" => _included, "excluded" => _excluded, _ => 0,
        };

        private MetadataDocument? Document(int? season, LocalMediaFile? file)
        {
            if (file != null)
            {
                if (!_episodeDocuments.TryGetValue(file, out var document))
                    _episodeDocuments[file] = document = MetadataEditing.Episode(Media, file, _resolved[file]);
                return document;
            }
            if (season is >= 0)
            {
                if (!_seasonDocuments.TryGetValue(season.Value, out var document))
                    _seasonDocuments[season.Value] = document = MetadataEditing.Season(Media, season.Value, _resolvedSeasons.GetValueOrDefault(season.Value) ?? Enumerable.Empty<EpisodeChoice>());
                return document;
            }
            return _metadata;
        }

        public string Value(int? season, LocalMediaFile? file, string column)
        {
            var media = Media; var resolved = file == null ? null : _resolved[file];
            var document = Document(season, file); var extra = file != null && _extras.Contains(file);
            if (column == "title") return file != null ? (file.Exclusion.Length > 0 || extra ? file.Name : $"{file.Episode}. {document?.Title ?? file.Name}") : season is int s ? s == -3 ? L.Text("Extra videos") : s == -2 ? L.Text("Needs episode matching / SP") : s < 0 ? L.Text("Excluded files") : media.Edits.Seasons.ContainsKey(s) ? $"S{s:00} · {document?.Title}" : s == 0 ? L.Text("Specials / SP") : L.Format($"Season {s}") : document?.Title ?? media.Title;
            return column switch
            {
                "year" => (document?.Year > 0 ? document.Year : media.Year).ToString(),
                "type" => L.Text(file != null ? file.Extra != null && resolved == null ? "Extra video" : "Episode" : season != null ? "Season" : media.Kind == LibraryMediaKind.Series ? "TV" : "Movie"),
                "match" => file != null ? L.Text(extra ? "Extra video" : file.NeedsReview ? "Needs manual matching" : file.Exclusion.Length > 0 ? "Excluded" : resolved == null ? "Not matched" : "Matched") : L.Text(season == -3 ? "Extra video" : season == -2 ? "Needs manual matching" : season == -1 ? "Excluded" : MatchStatus(season)),
                "nfo" => file != null ? file.NfoPath == null ? "—" : "✓" : season is >= 0 ? media.SeasonNfoPaths.ContainsKey(season.Value) ? "✓" : "—" : media.ScanWarning.Length > 0 ? L.Text("Needs attention") : media.NfoPath == null ? "—" : "✓",
                "poster" => (file != null ? file.ThumbPath : season is >= 0 ? media.SeasonArtworkPaths.GetValueOrDefault(season.Value)?.GetValueOrDefault("poster") : media.PosterPath) == null ? "—" : "✓",
                "original" => document?.OriginalTitle ?? "", "rating" => document?.Rating > 0 ? document.Rating.ToString("0.0") : "—",
                "source" => extra ? "—" : resolved?.Document.Provider ?? _sources,
                "seasons" => file != null ? extra ? "—" : file.Season.ToString() : season != null ? season.Value < 0 ? "—" : season.Value.ToString() : _includedPerSeason.Count.ToString(),
                "episodes" => file != null ? extra ? "—" : file.NeedsReview ? file.SourceNumber : file.Episode.ToString() : (season == -3 ? _extraCount : season == -2 ? _reviewCount : season < 0 ? _excludedGroup : season == null ? _included : _includedPerSeason.GetValueOrDefault(season.Value)).ToString(),
                "excluded" => _excludedWithoutReview.ToString(), "ids" => string.Join(" · ", (document?.Ids ?? media.ExistingIds).Select(pair => pair.Key + " " + pair.Value)),
                "path" => file?.Path ?? media.Folder, "filename" => file?.Name ?? Path.GetFileName(media.Folder), _ => "",
            };
        }
    }
}
