using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public static class MetadataEditing
{
    public static MetadataDocument Copy(MetadataDocument document) => JsonSerializer.Deserialize<MetadataDocument>(JsonSerializer.Serialize(document))!;
    public static MetadataDocument Work(LibraryMedia media) => media.Metadata ?? new() { Title = media.Title, Date = media.Year > 0 ? $"{media.Year}-01-01" : "", CreditsLoaded = true };

    public static MetadataDocument Season(LibraryMedia media, int number)
    {
        if (media.Edits.Seasons.TryGetValue(number, out var edited)) return edited;
        var resolver = new EpisodeResolutionIndex(media);
        return Season(media, number, media.Files.Where(f => f.Season == number).Select(resolver.Resolve).OfType<EpisodeChoice>());
    }

    public static MetadataDocument Season(LibraryMedia media, int number, IEnumerable<EpisodeChoice> resolvedEpisodes)
    {
        if (media.Edits.Seasons.TryGetValue(number, out var edited)) return edited;
        var match = media.Matches.GetValueOrDefault(number)?.Document;
        var resolved = resolvedEpisodes.ToList();
        var source = match ?? resolved.FirstOrDefault()?.Document;
        var sourceNumbers = resolved.Where(r => r.Document.Provider == source?.Provider && r.Document.Id == source?.Id).Select(r => r.Episode.Season).Distinct().ToList();
        var remoteNumber = sourceNumbers.Count == 1 ? sourceNumbers[0] : number;
        var season = source?.Seasons.FirstOrDefault(s => s.Number == remoteNumber);
        if (season == null && media.LocalSeasons.TryGetValue(number, out var local)) return local;
        var document = new MetadataDocument { Title = season?.Title ?? (number == 0 ? L.Text("Specials / SP") : L.Format($"Season {number}")), Overview = season?.Overview ?? "", Date = season?.Date ?? "", PosterUrl = season?.PosterUrl ?? "", Rating = season?.Rating ?? 0, CreditsLoaded = true };
        if (source?.Provider == "Bangumi" && number > 0)
        {
            document = new() { Provider = source.Provider, Id = source.Id, Ids = source.Ids, Title = season?.Title ?? source.Title, OriginalTitle = source.OriginalTitle, Overview = season?.Overview ?? source.Overview, OriginalOverview = source.OriginalOverview, OverviewLanguage = source.OverviewLanguage, Date = season?.Date ?? source.Date, PosterUrl = season?.PosterUrl ?? source.PosterUrl, Rating = source.Rating, VoteCount = source.VoteCount, Cast = source.Cast, Crew = source.Crew, Studios = source.Studios, Genres = source.Genres, CreditsLoaded = source.CreditsLoaded };
        }
        return document;
    }

    public static MetadataDocument Episode(LibraryMedia media, LocalMediaFile file)
    {
        if (media.Edits.Episodes.TryGetValue(file.Path, out var edited)) return edited;
        return Episode(media, file, EpisodeMatching.Resolve(media, file));
    }

    public static MetadataDocument Episode(LibraryMedia media, LocalMediaFile file, EpisodeChoice? resolved)
    {
        if (media.Edits.Episodes.TryGetValue(file.Path, out var edited)) return edited;
        if (resolved == null) return file.LocalMetadata ?? new() { Title = file.Name, CreditsLoaded = true };
        var episode = resolved.Episode;
        var source = resolved.Document;
        var doc = new MetadataDocument { Provider = source.Provider, OverviewLanguage = source.OverviewLanguage, Genres = source.Genres, Cast = source.Cast, Crew = source.Crew, Studios = source.Studios, CreditsLoaded = source.CreditsLoaded };
        doc.Id = episode.Id; doc.Ids = episode.Id.Length > 0 ? new() { [doc.Provider.ToLowerInvariant()] = episode.Id } : new();
        doc.Title = episode.Title; doc.OriginalTitle = episode.OriginalTitle; doc.Overview = episode.Overview;
        doc.OriginalOverview = ""; doc.OriginalOverviewLoaded = false; doc.Date = episode.Date; doc.Runtime = episode.Runtime;
        doc.Rating = episode.Rating ?? file.LocalMetadata?.Rating ?? 0; doc.VoteCount = episode.VoteCount;
        doc.PosterUrl = episode.ImageUrl; doc.BackdropUrl = doc.ClearLogoUrl = ""; doc.Tagline = "";
        return doc;
    }

    public static MetadataEpisode ApplyEpisodeEdit(LibraryMedia media, LocalMediaFile file, MetadataEpisode episode)
    {
        if (!media.Edits.Episodes.TryGetValue(file.Path, out var edit)) return episode;
        return episode with { Title = edit.Title, OriginalTitle = edit.OriginalTitle, Overview = edit.Overview, Date = edit.Date, Runtime = edit.Runtime, ImageUrl = edit.PosterUrl, Rating = edit.Rating, VoteCount = edit.VoteCount };
    }

    public static void Save(LibraryMedia media, MetadataDocument edited, int? season = null, LocalMediaFile? file = null)
    {
        Validate(edited);
        // Detach the entire draft (including actors and IDs) from provider caches.
        var value = Copy(edited); value.CreditsLoaded = true;
        if (file != null) media.Edits.Episodes[file.Path] = value;
        else if (season != null) media.Edits.Seasons[season.Value] = value;
        else media.Edits.Work = value;
    }

    public static void Validate(MetadataDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Title)) throw new ArgumentException(L.Text("Title is required."));
        if (!double.IsFinite(document.Rating) || document.Rating < 0 || document.Rating > 10) throw new ArgumentException(L.Text("Rating must be between 0 and 10."));
        if (document.VoteCount < 0 || document.Runtime < 0) throw new ArgumentException(L.Text("Use non-negative numbers."));
        if (document.Date.Length > 0 && !DateTime.TryParseExact(document.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException(L.Text("Use a date in yyyy-MM-dd format."));
    }

    public static void MoveFileEdits(MediaMetadataEdits edits, IEnumerable<FileRename> plan)
    {
        foreach (var rename in plan) if (edits.Episodes.Remove(rename.Source, out var document)) edits.Episodes[rename.Destination] = document;
    }

    public static MetadataDocument ReadNfo(XElement root, string folder)
    {
        var ids = root.Elements("uniqueid").Where(e => e.Attribute("type") != null).GroupBy(e => e.Attribute("type")!.Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var provider in new[] { "tmdb", "bangumi", "imdb", "tvdb" }) if (root.Element(provider + "id") is { } id) ids[provider] = id.Value;
        if (root.Element("imdb_id") is { } imdb) ids["imdb"] = imdb.Value;
        var source = root.Element("scraper");
        var providerName = source?.Element("provider")?.Value ?? root.Elements("uniqueid").FirstOrDefault(e => e.Attribute("default")?.Value == "true")?.Attribute("type")?.Value ?? (ids.ContainsKey("tmdb") ? "tmdb" : ids.ContainsKey("bangumi") ? "bangumi" : "");
        providerName = providerName.ToLowerInvariant() switch { "tmdb" => "TMDB", "bangumi" => "Bangumi", _ => providerName };
        var rating = root.Element("ratings")?.Elements("rating").FirstOrDefault(e => e.Attribute("default")?.Value == "true") ?? root.Element("ratings")?.Element("rating");
        double.TryParse(root.Element("rating")?.Value ?? rating?.Element("value")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score);
        int.TryParse(root.Element("votes")?.Value ?? rating?.Element("votes")?.Value, out var votes);
        int.TryParse(root.Element("runtime")?.Value, out var runtime);
        var doc = new MetadataDocument { Title = root.Element("title")?.Value ?? "", OriginalTitle = root.Element("originaltitle")?.Value ?? "", Overview = root.Element("plot")?.Value ?? "", Date = root.Element("aired")?.Value ?? root.Element("premiered")?.Value ?? root.Element("releasedate")?.Value ?? "", Rating = score, VoteCount = votes, Runtime = runtime, Tagline = root.Element("tagline")?.Value ?? "", Provider = providerName, Id = root.Name.LocalName == "episodedetails" ? ids.GetValueOrDefault(providerName.ToLowerInvariant(), "") : source?.Element("id")?.Value ?? ids.GetValueOrDefault(providerName.ToLowerInvariant(), ""), Ids = ids, Genres = root.Elements("genre").Select(e => e.Value).ToList(), Studios = root.Elements("studio").Select(e => e.Value).ToList(), CreditsLoaded = true };
        doc.Crew = root.Elements("director").Select(e => new CrewMember { Name = e.Value, Job = "Director" }).Concat(root.Elements("credits").Concat(root.Elements("writer")).Select(e => new CrewMember { Name = e.Value, Job = "Writer", Department = "Writing" })).ToList();
        doc.Cast = root.Elements("actor").Select(e =>
        {
            int.TryParse(e.Element(providerName.ToLowerInvariant() + "id")?.Value, out var id); int.TryParse(e.Element("order")?.Value, out var order);
            var photo = e.Element("thumb")?.Value ?? "";
            if (photo.Length > 0 && !(Uri.TryCreate(photo, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")) photo = Path.GetFullPath(Path.Combine(folder, photo));
            return new CastMember { Id = id, Name = e.Element("name")?.Value ?? "", Character = e.Element("role")?.Value ?? "", Order = order, ProfilePath = photo };
        }).ToList();
        doc.PosterUrl = root.Element("art")?.Element("poster")?.Value ?? root.Elements("thumb").FirstOrDefault(e => e.Attribute("aspect") == null || e.Attribute("aspect")?.Value == "poster")?.Value ?? "";
        doc.BackdropUrl = root.Element("art")?.Element("fanart")?.Value ?? root.Element("fanart")?.Element("thumb")?.Value ?? "";
        doc.ClearLogoUrl = root.Element("art")?.Element("clearlogo")?.Value ?? "";
        return doc;
    }
}
