using SimpleScraper.Models;

namespace SimpleScraper.Services;

/// <summary>
/// Indexes one media projection. Rebuild after changing documents, bindings,
/// episode edits or file numbering; the index deliberately has no global cache.
/// </summary>
public sealed class EpisodeResolutionIndex
{
    private readonly LibraryMedia media;
    private readonly Dictionary<MetadataDocument, DocumentIndex> documents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LocalMediaFile, EpisodeChoice?> results = new(ReferenceEqualityComparer.Instance);

    public EpisodeResolutionIndex(LibraryMedia media) => this.media = media ?? throw new ArgumentNullException(nameof(media));

    public EpisodeChoice? Resolve(LocalMediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!results.TryGetValue(file, out var result))
        {
            result = EpisodeMatching.ResolveCore(media, file,
                (document, id) => Get(document).ById.GetValueOrDefault(id),
                (document, season, number) => Get(document).ByNumber.GetValueOrDefault((season, number)));
            results.Add(file, result);
        }
        return result;
    }

    private DocumentIndex Get(MetadataDocument document)
    {
        if (documents.TryGetValue(document, out var indexed)) return indexed;
        indexed = new();
        foreach (var episode in document.Episodes)
        {
            indexed.ById.TryAdd(episode.Id, episode);
            var number = (episode.Season, episode.Number);
            // A duplicate number stays ambiguous regardless of further entries.
            if (!indexed.ByNumber.TryAdd(number, episode)) indexed.ByNumber[number] = null;
        }
        documents.Add(document, indexed);
        return indexed;
    }

    private sealed class DocumentIndex
    {
        public Dictionary<string, MetadataEpisode> ById { get; } = new(StringComparer.Ordinal);
        public Dictionary<(int Season, int Number), MetadataEpisode?> ByNumber { get; } = new();
    }
}
