namespace SimpleScraper.Models;

public enum LibraryMediaKind { Movie, Series, Auto }
public sealed record MediaLibrary(string Name, string Path, LibraryMediaKind Kind, bool IsRoot)
{
    public string? OutputProfile { get; init; }
    // Legacy entries retain their saved scanning mode; new libraries infer layout on refresh.
    public bool AutoLayout { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => L.Text(Kind switch { LibraryMediaKind.Movie => "Movies", LibraryMediaKind.Series => "TV series", _ => "Mixed / auto detect" });
    [System.Text.Json.Serialization.JsonIgnore]
    public string IconGlyph => "\uF12B";
    public override string ToString() => Name + " · " + L.Text(Kind switch { LibraryMediaKind.Movie => "Movies", LibraryMediaKind.Series => "TV series", _ => "Mixed / auto detect" });
}
public sealed class LibraryMedia
{
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public LibraryMediaKind Kind { get; set; }
    public List<LocalMediaFile> Files { get; set; } = new();
    public Dictionary<string, string> ExistingIds { get; set; } = new();
    public Dictionary<int, MetadataMatch> Matches { get; set; } = new();
    public Dictionary<string, MetadataDocument> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EpisodeBinding> EpisodeBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ScanWarning { get; set; } = "";
    public MetadataDocument? LocalMetadata { get; set; }
    public Dictionary<int, MetadataDocument> LocalSeasons { get; set; } = new();
    public MediaMetadataEdits Edits { get; set; } = new();
    public string? NfoPath { get; set; }
    public string? PosterPath { get; set; }
    public Dictionary<string, string> ArtworkPaths { get; set; } = new();
    public Dictionary<int, Dictionary<string, string>> SeasonArtworkPaths { get; set; } = new();
    public Dictionary<int, string> SeasonNfoPaths { get; set; } = new();
    public MetadataDocument? Metadata => Edits.Work ?? Matches.OrderBy(p => p.Key == 0 ? int.MaxValue : p.Key).FirstOrDefault().Value?.Document ?? LocalMetadata;
    public string Display => $"{Title} {(Year > 0 ? $"({Year})" : "")} · {L.Text(Kind == LibraryMediaKind.Series ? "TV series" : "Movie")} · {Files.Count(f => f.Exclusion.Length == 0)}";
    public override string ToString() => Display;
}
public sealed class LocalMediaFile
{
    public string Path { get; set; } = "";
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Exclusion { get; set; } = "";
    public bool NeedsReview { get; set; }
    public string SourceNumber { get; set; } = "";
    public MediaExtraInfo? Extra { get; set; }
    public bool ManuallyExcluded { get; set; }
    public string? NfoPath { get; set; }
    public string? ThumbPath { get; set; }
    public MetadataDocument? LocalMetadata { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
}
public sealed record MetadataCandidate(string Provider, string Id, string Title, string OriginalTitle, int Year, string Overview, string PosterUrl)
{
    public override string ToString() => $"{Title} ({Year}) · {OriginalTitle} · {Provider} #{Id}";
}
public sealed record MediaExtraInfo(string Tag);
public sealed class MetadataDocument
{
    public string Provider { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Overview { get; set; } = "";
    public string OverviewLanguage { get; set; } = "";
    public string OriginalOverview { get; set; } = "";
    public bool OriginalOverviewLoaded { get; set; }
    public string Date { get; set; } = "";
    public double Rating { get; set; }
    public int VoteCount { get; set; }
    public int Runtime { get; set; }
    public string Tagline { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string BackdropUrl { get; set; } = "";
    public string ClearLogoUrl { get; set; } = "";
    public Dictionary<string, string> Ids { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<CastMember> Cast { get; set; } = new();
    public List<CrewMember> Crew { get; set; } = new();
    public List<string> Studios { get; set; } = new();
    public bool CreditsLoaded { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<MetadataEpisode> Episodes { get; set; } = new();
    public List<MetadataSeason> Seasons { get; set; } = new();
    public int Year => DateTime.TryParse(Date, out var d) ? d.Year : 0;
}
public sealed record MetadataEpisode(string Id, int Season, int Number, string Title, string Overview, string Date, int Runtime, string ImageUrl, string SourceNumber = "", string OriginalTitle = "")
{
    public double? Rating { get; init; }
    public int VoteCount { get; init; }
}
public sealed record MetadataSeason(int Number, string Title, string Overview, string Date, string PosterUrl)
{
    public double? Rating { get; init; }
}
public sealed class MediaMetadataEdits
{
    public MetadataDocument? Work { get; set; }
    public Dictionary<int, MetadataDocument> Seasons { get; set; } = new();
    public Dictionary<string, MetadataDocument> Episodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed record MetadataMatch(MetadataCandidate Candidate, MetadataDocument Document);
public sealed record ManualFileMapping(int Season, int Episode, bool Excluded, bool ExplicitExclusion = false);
public sealed record EpisodeBinding(string SourceKey, MetadataEpisode Episode, int Season, int Number);
public sealed record FileRename(string Source, string Destination)
{
    public int? OutputSeason { get; init; }
    public int? OutputEpisode { get; init; }
    public override string ToString() => $"{System.IO.Path.GetFileName(Source)}\n→ {System.IO.Path.GetFileName(Destination)}";
}
public sealed record FolderRename(string Source, string Destination);
