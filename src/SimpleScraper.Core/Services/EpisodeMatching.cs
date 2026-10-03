using SimpleScraper.Models;

namespace SimpleScraper.Services;

public sealed record EpisodeChoice(string SourceKey, MetadataDocument Document, MetadataEpisode Episode)
{
    public string NumberLabel => string.IsNullOrEmpty(Episode.SourceNumber) ? $"S{Episode.Season:00}E{Episode.Number:00}" : "SP · " + Episode.SourceNumber;
    public string Label => $"{NumberLabel} · {Episode.Title} · {Document.Provider} #{Document.Id}";
}
public static class EpisodeMatching
{
    public static string Key(MetadataDocument doc, LibraryMediaKind kind) => $"{kind}/{doc.Provider}/{doc.Id}";
    public static EpisodeResolutionIndex CreateResolver(LibraryMedia media) => new(media);
    public static EpisodeChoice? Resolve(LibraryMedia media, LocalMediaFile file) => ResolveCore(media, file,
        (document, id) => document.Episodes.FirstOrDefault(e => e.Id == id),
        (document, season, number) =>
        {
            var episodes = document.Episodes.Where(e => e.Season == season && e.Number == number).ToList();
            return episodes.Count == 1 ? episodes[0] : null;
        });
    internal static EpisodeChoice? ResolveCore(LibraryMedia media, LocalMediaFile file,
        Func<MetadataDocument, string, MetadataEpisode?> firstById,
        Func<MetadataDocument, int, int, MetadataEpisode?> uniqueByNumber)
    {
        if (file.Exclusion.Length > 0) return null;
        if (media.EpisodeBindings.TryGetValue(file.Path, out var binding))
        {
            if (!media.Documents.TryGetValue(binding.SourceKey, out var document)) return null;
            var episode = binding.Episode;
            if (episode.Rating == null && firstById(document, episode.Id) is { Rating: not null } rated) episode = episode with { Rating = rated.Rating, VoteCount = rated.VoteCount };
            return new(binding.SourceKey, document, MetadataEditing.ApplyEpisodeEdit(media, file, episode));
        }
        if (!media.Matches.TryGetValue(file.Season, out var match)) return null;
        var matched = uniqueByNumber(match.Document, file.Season, file.Episode);
        return matched == null ? null : new(Key(match.Document, media.Kind), match.Document, MetadataEditing.ApplyEpisodeEdit(media, file, matched));
    }
    public static List<EpisodeChoice> Choices(IEnumerable<MetadataDocument> documents, LibraryMediaKind kind) => documents
        .SelectMany(d => d.Episodes.Select(e => new EpisodeChoice(Key(d, kind), d, e)))
        .DistinctBy(c => (c.SourceKey, c.Episode.Id)).OrderBy(c => c.Episode.Season).ThenBy(c => c.Episode.Number).ThenBy(c => c.SourceKey).ToList();
    public static Dictionary<string, EpisodeBinding> Suggest(LibraryMedia media, IEnumerable<MetadataDocument> documents, IReadOnlySet<int> seasons, bool absolute)
    {
        var choices = Choices(documents, media.Kind); var regular = choices.Where(c => c.Episode.Season > 0).GroupBy(c => (c.Episode.Season, c.Episode.Number)).ToList();
        var result = new Dictionary<string, EpisodeBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in media.Files.Where(f => f.Exclusion.Length == 0 && seasons.Contains(f.Season)))
        {
            var options = absolute && file.Season > 0 ? regular.Skip(Math.Max(0, file.Episode - 1)).Take(file.Episode > 0 ? 1 : 0).SelectMany(g => g).ToList() : choices.Where(c => c.Episode.Season == file.Season && c.Episode.Number == file.Episode).ToList();
            // Ambiguous candidates never silently become a match.
            if (options.Count == 1) { var c = options[0]; result[file.Path] = new(c.SourceKey, c.Episode, c.Episode.Season, c.Episode.Number); }
        }
        return result;
    }
    public static List<string> Conflicts(IEnumerable<KeyValuePair<string, EpisodeBinding>> bindings)
    {
        var values = bindings.ToList(); var conflicts = new List<string>();
        foreach (var group in values.GroupBy(p => (p.Value.Season, p.Value.Number)).Where(g => g.Count() > 1)) conflicts.Add($"S{group.Key.Season:00}E{group.Key.Number:00}: " + string.Join(" / ", group.Select(p => Path.GetFileName(p.Key))));
        foreach (var group in values.GroupBy(p => (p.Value.SourceKey, p.Value.Episode.Id)).Where(g => g.Count() > 1)) conflicts.Add(L.Text("One source episode is assigned to multiple files") + ": " + string.Join(" / ", group.Select(p => Path.GetFileName(p.Key))));
        return conflicts.Distinct().ToList();
    }
    public static Dictionary<string, EpisodeBinding> SuggestForResult(LibraryMedia media, MetadataDocument document, IReadOnlySet<int> localSeasons, int? remoteSeason, bool absolute)
    {
        if (!CanSuggestForResult(document, localSeasons)) return new(StringComparer.OrdinalIgnoreCase);
        var result = Suggest(media, new[] { document }, localSeasons, absolute);
        if (!absolute && localSeasons.Count == 1 && remoteSeason is int remote && !localSeasons.Contains(remote))
        {
            result.Clear();
            var local = localSeasons.Single();
            foreach (var file in media.Files.Where(f => f.Exclusion.Length == 0 && f.Season == local))
            {
                var episodes = document.Episodes.Where(e => e.Season == remote && e.Number == file.Episode).ToList();
                if (episodes.Count == 1) result[file.Path] = new(Key(document, media.Kind), episodes[0], local, episodes[0].Number);
            }
        }
        return result;
    }
    public static bool CanSuggestForResult(MetadataDocument document, IReadOnlySet<int> localSeasons) => document.Provider != "Bangumi" || localSeasons.Count == 1;
}
