using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public sealed record ScrapeReport(int Written, int Preserved, int Images, List<string> Warnings);

public sealed class LibraryOutputService(ImageDownloadService images, TmdbApiClient? artworkClient = null, BangumiMetadataProvider? bangumiClient = null, string? peopleDirectory = null)
{
    public Task<ScrapeReport> UpdateMetadataAsync(LibraryMedia media, MediaOutputProfile profile, bool episodesOnly = false, int? seasonOnly = null, IProgress<string>? progress = null) => ScrapeAsync(media, profile, episodesOnly: episodesOnly, seasonOnly: seasonOnly, progress: progress, downloadArtwork: false, downloadActorPhotos: false, metadataOnly: true);
    public async Task<ScrapeReport> ScrapeAsync(LibraryMedia media, MediaOutputProfile profile, bool updateExisting = true, bool episodesOnly = false, IProgress<string>? progress = null, int? seasonOnly = null, bool downloadArtwork = true, bool downloadActorPhotos = true, bool metadataOnly = false)
    {
        if (!metadataOnly && media.Matches.Count == 0) throw new InvalidOperationException(L.Text("Confirm a metadata match first."));
        var duplicate = media.Files.Where(f => f.Exclusion.Length == 0 && media.Kind == LibraryMediaKind.Series).GroupBy(f => (f.Season, f.Episode)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException(L.Format($"Multiple files mapped to S{duplicate.Key.Season:00}E{duplicate.Key.Episode:00}. Resolve the duplicate mapping first."));
        var written = 0; var preserved = 0; var downloaded = 0;
        var warnings = new System.Collections.Concurrent.ConcurrentBag<string>();
        var primary = MetadataEditing.Work(media);
        var people = new ActorImageCache(peopleDirectory);
        var tv = media.Kind == LibraryMediaKind.Series;
        foreach (var document in media.Matches.Values.Select(m => m.Document).Concat(media.Documents.Values).Where(_ => !metadataOnly).DistinctBy(d => (d.Provider, d.Id)))
        {
            progress?.Report(L.Text("Loading cast and production credits…"));
            try { await MetadataCredits.EnsureAsync(document, media.Kind, artworkClient, bangumiClient); }
            catch (Exception) { warnings.Add(L.Text("Cast and production credits could not be loaded. Existing matches are preserved.")); }
            // Older matching records discarded episode vote fields. Recover them
            // during a normal scrape; a local NFO update never requests them.
            if (tv && document.Provider == "TMDB" && artworkClient != null && int.TryParse(document.Id, out var seriesId))
            {
                var incomplete = media.Files.Where(f => f.Exclusion.Length == 0 && (seasonOnly == null || f.Season == seasonOnly))
                    .Select(f => EpisodeMatching.Resolve(media, f)).OfType<EpisodeChoice>()
                    .Where(r => r.Document.Provider == document.Provider && r.Document.Id == document.Id && r.Episode.Rating == null)
                    .Select(r => r.Episode.Season).Distinct().ToList();
                foreach (var remoteSeason in incomplete)
                {
                    try
                    {
                        var loaded = await artworkClient.GetTvSeasonDetailsAsync(seriesId, remoteSeason);
                        foreach (var episode in loaded.Episodes)
                        {
                            var index = document.Episodes.FindIndex(e => e.Id == episode.TmdbId.ToString());
                            if (index >= 0) document.Episodes[index] = document.Episodes[index] with { Rating = episode.Rating, VoteCount = episode.VoteCount };
                        }
                    }
                    catch (Exception exception) { warnings.Add($"S{remoteSeason:00}: " + exception.Message); }
                }
            }
            foreach (var warning in document.Warnings.Distinct()) warnings.Add(warning);
        }
        if (downloadActorPhotos)
        {
            var affected = tv ? media.Files.Where(f => f.Exclusion.Length == 0 && (seasonOnly == null || f.Season == seasonOnly)).Select(f => MetadataEditing.Episode(media, f)) : new[] { primary };
            if (!episodesOnly && seasonOnly == null) affected = affected.Append(primary);
            if (!episodesOnly && seasonOnly != null) affected = affected.Append(MetadataEditing.Season(media, seasonOnly.Value));
            var cast = affected.SelectMany(d => d.Cast.Select(a => (Document: d, Actor: a)))
                .DistinctBy(p => people.ImagePath(p.Document.Provider, p.Actor.Id, p.Actor.Name, p.Actor.ProfilePath), StringComparer.OrdinalIgnoreCase);
            using var photoGate = new SemaphoreSlim(4);
            await Task.WhenAll(cast.Select(async person =>
            {
                await photoGate.WaitAsync();
                try
                {
                    var actor = person.Actor; var provider = person.Document.Provider;
                    progress?.Report(L.Text("Actor photos") + " · " + actor.Name);
                    var cached = await people.EnsureAsync(provider, actor.Id, actor.Name, actor.ProfilePath, images);
                    if (cached.Downloaded) Interlocked.Increment(ref downloaded);
                    if (cached.LocalPath == null && ActorImageCache.RemoteUrl(provider, actor.ProfilePath).Length > 0) warnings.Add(actor.Name + ": " + L.Text("Image download failed."));
                }
                catch (Exception) { warnings.Add(person.Actor.Name + ": " + L.Text("Actor photo could not be cached in the app folder.")); }
                finally { photoGate.Release(); }
            }));
        }
        var included = media.Files.FirstOrDefault(f => f.Exclusion.Length == 0);
        if (included == null) throw new InvalidOperationException(L.Text("Select an included episode first."));
        var baseName = Path.GetFileNameWithoutExtension(included.Path);
        var nfoPath = Path.Combine(media.Folder, tv ? "tvshow.nfo" : profile.FolderMovieNfo ? "movie.nfo" : baseName + ".nfo");
        // Honor an existing movie NFO even when switching preferred output conventions.
        if (!tv) nfoPath = LibraryAssets.FindNfo(media.Folder) ?? nfoPath;
        var root = Common(primary, tv ? "tvshow" : "movie", profile, tv);
        if (tv && primary.Provider == "Bangumi" && media.Edits.Work == null) root.SetElementValue("title", media.Title);
        var firstLocalSeason = media.Files.Where(f => f.Exclusion.Length == 0 && f.Season > 0).Select(f => f.Season).DefaultIfEmpty(1).Min();
        if (!metadataOnly && !episodesOnly && seasonOnly == null && tv && primary.Provider == "Bangumi" && !media.Matches.ContainsKey(firstLocalSeason)) warnings.Add(L.Text("Match the first local season before exporting series-level Bangumi metadata. Matched episodes will still be exported."));
        else if (!episodesOnly && seasonOnly == null)
        {
            var poster = LibraryAssets.FindArtwork(media.Folder, "poster") ?? Path.Combine(media.Folder, "poster.jpg");
            var backdrop = LibraryAssets.FindArtwork(media.Folder, Path.GetFileNameWithoutExtension(profile.BackdropName)) ?? Path.Combine(media.Folder, profile.BackdropName);
            await Art(primary.PosterUrl, poster); await Art(primary.BackdropUrl, backdrop);
            foreach (var reference in root.Descendants().Where(e => !e.HasElements && e.Name.LocalName is "poster" or "thumb" or "fanart"))
            {
                if (reference.Value == "poster.jpg") reference.Value = File.Exists(poster) ? Path.GetFileName(poster) : primary.PosterUrl;
                if (reference.Value == profile.BackdropName) reference.Value = File.Exists(backdrop) ? Path.GetFileName(backdrop) : primary.BackdropUrl;
            }
            var logo = LibraryAssets.FindArtwork(media.Folder, "clearlogo");
            if (logo == null && downloadArtwork)
            {
                progress?.Report(L.Text("Clearlogo"));
                var logoUrl = primary.ClearLogoUrl;
                var tmdbId = primary.Provider == "TMDB" ? primary.Id : primary.Ids.GetValueOrDefault("tmdb");
                if (logoUrl.Length == 0 && artworkClient != null && int.TryParse(tmdbId, out var number) && number > 0)
                {
                    try { primary.ClearLogoUrl = logoUrl = await artworkClient.GetClearLogoAsync(number, tv); }
                    catch (Exception) { warnings.Add(L.Text("Clearlogo could not be loaded from the selected source.")); }
                }
                if (logoUrl.Length > 0)
                {
                    var path = Path.Combine(media.Folder, "clearlogo.png");
                    if (await images.DownloadPngAsync(logoUrl, path)) { downloaded++; logo = path; }
                    else warnings.Add("clearlogo.png: " + L.Text("Image download failed."));
                }
            }
            if (logo != null)
            {
                var art = root.Element("art"); if (art == null) { art = new XElement("art"); root.Add(art); }
                art.SetElementValue("clearlogo", Path.GetFileName(logo));
            }
            Save(nfoPath, root, media.Edits.Work != null);
        }
        if (tv)
        {
            foreach (var file in media.Files.Where(f => f.Exclusion.Length == 0 && (seasonOnly == null || f.Season == seasonOnly)))
            {
                progress?.Report(file.Name);
                var resolved = EpisodeMatching.Resolve(media, file);
                if (resolved == null && !(metadataOnly && (file.LocalMetadata != null || media.Edits.Episodes.ContainsKey(file.Path)))) { warnings.Add(file.Name + ": " + L.Text("No unique episode match.")); continue; }
                if (file.Season < 0 || file.Episode <= 0 || file.NeedsReview && resolved == null) { warnings.Add(file.Name + ": " + L.Text("Use integer season and episode numbers.")); continue; }
                var metadata = MetadataEditing.Episode(media, file);
                var ep = Common(metadata, "episodedetails", profile, false);
                ep.Element("releasedate")?.Remove(); ep.Element("scraper")?.Remove();
                ep.SetElementValue("aired", metadata.Date); ep.SetElementValue("showtitle", media.Edits.Work?.Title ?? media.Title); ep.SetElementValue("season", file.Season); ep.SetElementValue("episode", file.Episode);
                if (resolved != null) AddSource(ep, resolved.Document, media.Kind, resolved.Episode);
                var thumb = file.ThumbPath ?? LibraryAssets.FindNamedArtwork(Path.GetDirectoryName(file.Path)!, Path.GetFileNameWithoutExtension(file.Path) + "-thumb") ?? Path.ChangeExtension(file.Path, null) + "-thumb.jpg";
                await Art(metadata.PosterUrl, thumb);
                ep.Element("art")?.Remove(); ep.Elements("thumb").Remove();
                if (File.Exists(thumb) || metadata.PosterUrl.Length > 0) ep.Add(new XElement("thumb", File.Exists(thumb) ? Path.GetFileName(thumb) : metadata.PosterUrl));
                Save(Path.ChangeExtension(file.Path, ".nfo"), ep, media.Edits.Episodes.ContainsKey(file.Path));
            }
            foreach (var season in media.Files.Where(f => !episodesOnly && f.Exclusion.Length == 0 && (seasonOnly == null || f.Season == seasonOnly)).GroupBy(f => f.Season))
            {
                var resolved = season.Select(f => EpisodeMatching.Resolve(media, f)).Where(r => r != null).ToList();
                var document = media.Matches.GetValueOrDefault(season.Key)?.Document ?? resolved.FirstOrDefault()?.Document;
                if (document == null && !metadataOnly) continue;
                var sourceNumbers = resolved.Where(r => r!.Document.Provider == document?.Provider && r.Document.Id == document?.Id).Select(r => r!.Episode.Season).Distinct().ToList();
                var sourceSeason = sourceNumbers.Count == 1 ? sourceNumbers[0] : season.Key;
                var details = document?.Seasons.FirstOrDefault(s => s.Number == sourceSeason);
                if (details == null && downloadArtwork && document?.Provider == "TMDB" && artworkClient != null && int.TryParse(document.Id, out var tvId))
                {
                    try { var info = await artworkClient.GetTvSeasonDetailsAsync(tvId, sourceSeason); details = new(sourceSeason, info.Name, info.Overview, info.AirDate, artworkClient.GetImageUrl(info.PosterPath)); document.Seasons.Add(details); }
                    catch (Exception) { warnings.Add($"S{season.Key:00}: " + L.Text("Season artwork could not be loaded.")); }
                }
                var seasonMetadata = MetadataEditing.Season(media, season.Key);
                var folders = metadataOnly && media.SeasonNfoPaths.TryGetValue(season.Key, out var existingSeasonNfo) ? new List<string> { Path.GetDirectoryName(existingSeasonNfo)! } : season.Select(f => profile.EpisodeFolder(media, f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var folder in folders)
                {
                    Directory.CreateDirectory(folder);
                    var poster = LibraryAssets.FindNamedArtwork(folder, "poster") ?? LibraryAssets.FindNamedArtwork(folder, "folder");
                    // Retain user-supplied legacy flat season art without downloading a replacement.
                    var legacyPoster = LibraryAssets.FindNamedArtwork(media.Folder, season.Key == 0 ? "season-specials-poster" : $"season{season.Key:00}-poster");
                    if (poster == null && legacyPoster != null && downloadArtwork)
                    {
                        poster = Path.Combine(folder, "poster" + Path.GetExtension(legacyPoster));
                        File.Copy(legacyPoster, poster, false);
                    }
                    poster ??= Path.Combine(folder, "poster.jpg");
                    progress?.Report(L.Format($"Season {season.Key}") + " · " + L.Text("Poster"));
                    await Art(seasonMetadata.PosterUrl, poster);
                    var nfo = Common(seasonMetadata, "season", profile, false); nfo.Element("releasedate")?.Remove(); nfo.Element("art")?.Remove(); nfo.Elements("thumb").Remove();
                    nfo.SetElementValue("seasonnumber", season.Key); nfo.SetElementValue("premiered", seasonMetadata.Date);
                    if (document != null && nfo.Element("scraper") == null) AddSource(nfo, document, media.Kind);
                    if (File.Exists(poster)) nfo.Add(new XElement("thumb", new XAttribute("aspect", "poster"), Path.GetFileName(poster)));
                    else if (seasonMetadata.PosterUrl.Length > 0) nfo.Add(new XElement("thumb", new XAttribute("aspect", "poster"), seasonMetadata.PosterUrl));
                    Save(Path.Combine(folder, "season.nfo"), nfo, media.Edits.Seasons.ContainsKey(season.Key));
                }
            }
        }
        return new(written, preserved, downloaded, warnings.Distinct().ToList());

        void Save(string path, XElement content, bool edited = false)
        {
            if (File.Exists(path) && !updateExisting) { preserved++; return; }
            WriteNfo(path, content, updateExisting, edited); written++;
        }
        async Task<bool> Art(string url, string path, bool actor = false)
        {
            if (File.Exists(path)) return true;
            if ((!downloadArtwork && !actor) || url.Length == 0) return false;
            if (await images.DownloadAnyImageAsync(url, path)) { Interlocked.Increment(ref downloaded); return true; }
            else warnings.Add(Path.GetFileName(path) + ": " + L.Text("Image download failed."));
            return false;
        }
    }
    private static XElement Common(MetadataDocument m, string element, MediaOutputProfile profile, bool tv)
    {
        var root = new XElement(element, new XElement("title", m.Title), new XElement("originaltitle", m.OriginalTitle), new XElement("plot", m.Overview), new XElement("year", m.Year), new XElement(tv ? "premiered" : "releasedate", m.Date), new XElement("rating", m.Rating.ToString(CultureInfo.InvariantCulture)));
        root.Add(new XElement("votes", m.VoteCount), new XElement("runtime", m.Runtime), new XElement("tagline", m.Tagline));
        root.Add(new XElement("ratings", new XElement("rating", new XAttribute("name", m.Provider.Length > 0 ? m.Provider.ToLowerInvariant() : "custom"), new XAttribute("max", "10"), new XAttribute("default", "true"), new XElement("value", m.Rating.ToString(CultureInfo.InvariantCulture)), new XElement("votes", m.VoteCount))));
        var ids = new Dictionary<string, string>(m.Ids, StringComparer.OrdinalIgnoreCase);
        if (m.Provider.Length > 0 && m.Id.Length > 0) ids[m.Provider.ToLowerInvariant()] = m.Id;
        foreach (var id in ids)
        {
            root.Add(new XElement("uniqueid", new XAttribute("type", id.Key), new XAttribute("default", id.Key == m.Provider.ToLowerInvariant() ? "true" : "false"), id.Value));
            root.Add(new XElement(tv && id.Key == "imdb" ? profile.TvImdbTag : id.Key + "id", id.Value));
        }
        foreach (var g in m.Genres) root.Add(new XElement("genre", g));
        if (m.Provider.Length > 0 && m.Id.Length > 0) AddSource(root, m, tv ? LibraryMediaKind.Series : LibraryMediaKind.Movie);
        AddCredits(root, m);
        var art = new XElement("art");
        if (m.PosterUrl.Length > 0) art.Add(new XElement("poster", "poster.jpg"));
        if (m.BackdropUrl.Length > 0) art.Add(new XElement("fanart", profile.BackdropName));
        if (profile.Name == "Jellyfin") root.Add(art);
        else
        {
            if (m.PosterUrl.Length > 0) root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), "poster.jpg"));
            if (m.BackdropUrl.Length > 0) root.Add(new XElement("fanart", new XElement("thumb", profile.BackdropName)));
        }
        return root;
    }
    private static void AddSource(XElement root, MetadataDocument document, LibraryMediaKind kind, MetadataEpisode? episode = null)
    {
        // Supplement standard originaltitle / uniqueid with provenance for
        // season subjects and source numbering. Media servers can ignore this
        // app-specific block without mistaking a show's ID for an episode ID.
        var source = new XElement("scraper", new XElement("provider", document.Provider), new XElement("id", document.Id), new XElement("title", document.Title), new XElement("originaltitle", document.OriginalTitle));
        if (MetadataLinks.Get(document.Provider, document.Id, kind) is { } url) source.Add(new XElement("url", url.AbsoluteUri));
        if (episode != null) source.Add(new XElement("episodeid", episode.Id), new XElement("season", episode.Season), new XElement("episode", episode.SourceNumber.Length > 0 ? episode.SourceNumber : episode.Number.ToString(CultureInfo.InvariantCulture)));
        root.Add(source);
    }
    private static void AddCredits(XElement root, MetadataDocument document)
    {
        foreach (var cast in document.Cast)
        {
            var actor = new XElement("actor", new XElement("name", cast.Name), new XElement("role", cast.Character), new XElement("type", "Actor"), new XElement("order", cast.Order));
            if (cast.Id > 0 && document.Provider is "TMDB" or "Bangumi") actor.Add(new XElement(document.Provider.ToLowerInvariant() + "id", cast.Id));
            var url = ActorImageCache.RemoteUrl(document.Provider, cast.ProfilePath);
            if (url.Length > 0) actor.Add(new XElement("thumb", url));
            root.Add(actor);
        }
        foreach (var studio in document.Studios.Where(s => s.Length > 0).Distinct()) root.Add(new XElement("studio", studio));
        foreach (var name in document.Crew.Where(p => p.Job is "Director" or "导演" or "总导演" or "監督").Select(p => p.Name).Distinct()) root.Add(new XElement("director", name));
        foreach (var name in document.Crew.Where(p => p.Department == "Writing" || p.Job is "Writer" or "Screenplay" or "Creator" or "脚本" or "系列构成").Select(p => p.Name).Distinct()) root.Add(new XElement("credits", name));
    }
    public static void WriteNfo(string path, XElement content, bool update = true, bool clearEditableCollections = false)
    {
        var exists = File.Exists(path);
        if (exists && !update) return;
        if (exists)
        {
            XElement? old = null;
            try { old = XDocument.Load(path).Root; }
            catch (System.Xml.XmlException) { /* Regenerate malformed NFO from confirmed metadata. */ }
            if (old != null)
            {
            if (old.Name != content.Name) throw new InvalidDataException(L.Text("Existing NFO type differs from the selected media type."));
            // Preserve stream details and custom fields. Provider IDs must come from the confirmed metadata,
            // as old hand-edited NFOs can contain IDs pointing to a completely different show.
            var names = content.Elements().Select(e => e.Name).Distinct().ToHashSet();
            foreach (var element in old.Elements().ToList())
            {
                if (element.Name.LocalName is "uniqueid" or "tmdbid" or "bangumiid" or "imdbid" or "imdb_id" or "tvdbid" or "id" or "art" or "thumb" or "fanart" or "scraper" or "ratings" or "rating" or "votes") continue;
                if (clearEditableCollections && element.Name.LocalName is "actor" or "director" or "credits" or "writer" or "genre" or "studio") continue;
                if (!names.Contains(element.Name)) content.Add(new XElement(element));
            }
            }
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new XDocument(new XDeclaration("1.0", "utf-8", null), content).ToString(), new UTF8Encoding(false));
            if (exists)
            {
                File.Replace(temporary, path, null);
            }
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class LibraryRenameService(string? historyDirectory = null)
{
    public const string MovieTemplate = "{Title} ({Year})";
    public const string EpisodeTemplate = "{Title} - S{Season}E{Episode} - {EpisodeTitle}";
    public const string WorkFolderTemplate = "{Title} ({Year})";
    public static string WorkFolderName(LibraryMedia media, string template = WorkFolderTemplate)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException(L.Text("Empty filename."));
        if (Regex.Matches(template, @"\{([^{}]+)\}").Any(m => m.Groups[1].Value is not ("Title" or "Year" or "OriginalTitle")) ||
            Regex.Replace(template, @"\{(?:Title|Year|OriginalTitle)\}", "").IndexOfAny(new[] { '{', '}' }) >= 0)
            throw new ArgumentException(L.Text("Unknown rename template token."));
        // TMDB TV documents describe the whole work; Bangumi subjects often describe one season.
        var document = media.Edits.Work ?? media.Matches.Values.Select(m => m.Document).FirstOrDefault(d => d.Provider == "TMDB") ?? media.Metadata;
        var title = string.IsNullOrWhiteSpace(document?.Title) ? media.Title : document.Title;
        if (media.Kind == LibraryMediaKind.Series && media.Edits.Work == null && document?.Provider == "Bangumi")
            title = Regex.Replace(title, @"\s*(?:第[一二三四五六七八九十百\d]+季|Season\s*\d+|S\d{1,3})$", "", RegexOptions.IgnoreCase).Trim();
        var year = document?.Year > 0 ? document.Year : media.Year;
        var values = new Dictionary<string, string> { ["Title"] = title, ["OriginalTitle"] = string.IsNullOrWhiteSpace(document?.OriginalTitle) ? title : document.OriginalTitle, ["Year"] = year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "" };
        // Replace in one pass so braces in a source title cannot become template tokens.
        var name = Regex.Replace(template, @"\{(Title|Year|OriginalTitle)\}", m => values[m.Groups[1].Value]);
        if (year <= 0) name = Regex.Replace(name, @"\(\s*\)|\[\s*\]", "");
        name = SafeName(name);
        if (name.Length == 0) throw new ArgumentException(L.Text("Empty filename."));
        return name;
    }
    public static FolderRename? PreviewFolder(LibraryMedia media, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name != SafeName(name) || name is "." or "..") throw new ArgumentException(L.Text("Invalid folder name."));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(media.Folder));
        var parent = Path.GetDirectoryName(source) ?? throw new IOException(L.Text("Invalid folder name."));
        var destination = Path.Combine(parent, name);
        return source.Equals(destination, StringComparison.OrdinalIgnoreCase) ? null : new(source, destination);
    }
    private static bool IsOrganizableExtra(LibraryMedia media, LocalMediaFile file) => (file.Extra ?? ExtraMedia.Identify(media.Folder, file.Path)) != null &&
        // Old comparison drafts recorded automatically skipped OP/ED as 0/0
        // exclusions. Their episode skip must not block previewed extras moves.
        !(file.Episode > 0 && (file.ManuallyExcluded || file.Exclusion == L.Text("Manually excluded"))) &&
        !media.EpisodeBindings.ContainsKey(file.Path) && (media.Kind != LibraryMediaKind.Series || EpisodeMatching.Resolve(media, file) == null);
    public List<FileRename> Preview(LibraryMedia media, string template, bool validate = true, IReadOnlyDictionary<string, string[]>? folderFiles = null, MediaOutputProfile? profile = null, bool organizeSpecials = true)
    {
        if (media.Matches.Count == 0 && !media.Files.Any(f => IsOrganizableExtra(media, f))) throw new InvalidOperationException(L.Text("Confirm a metadata match first."));
        var duplicate = media.Files.Where(f => f.Exclusion.Length == 0 && !IsOrganizableExtra(media, f) && media.Kind == LibraryMediaKind.Series).GroupBy(f => (f.Season, f.Episode)).FirstOrDefault(g => g.Count() > 1);
        if (validate && duplicate != null) throw new InvalidOperationException(L.Format($"Multiple files mapped to S{duplicate.Key.Season:00}E{duplicate.Key.Episode:00}. Resolve the duplicate mapping first."));
        var tokens = Regex.Matches(template, @"\{([^{}]+)\}").Select(m => m.Groups[1].Value);
        if (media.Files.Any(f => f.Exclusion.Length == 0 && !IsOrganizableExtra(media, f)))
        {
            if (tokens.Any(t => t is not ("Title" or "Year" or "Season" or "Episode" or "EpisodeTitle"))) throw new ArgumentException(L.Text("Unknown rename template token."));
            if (media.Kind == LibraryMediaKind.Series && (!template.Contains("{Season}") || !template.Contains("{Episode}"))) throw new ArgumentException(L.Text("Episode templates must include {Season} and {Episode}."));
        }
        var plan = new List<FileRename>();
        foreach (var file in media.Files.Where(f => f.Exclusion.Length == 0 || IsOrganizableExtra(media, f)))
        {
            var extra = IsOrganizableExtra(media, file);
            var key = media.Kind == LibraryMediaKind.Movie ? -1 : file.Season;
            var resolved = media.Kind == LibraryMediaKind.Series ? EpisodeMatching.Resolve(media, file) : null;
            media.Matches.TryGetValue(key, out var match);
            if (!extra && (media.Kind == LibraryMediaKind.Series && resolved == null || match == null && resolved == null)) continue;
            var m = media.Edits.Work ?? resolved?.Document ?? match?.Document;
            var title = media.Kind == LibraryMediaKind.Series && m?.Provider == "Bangumi" && media.Edits.Work == null ? media.Title : m?.Title ?? media.Title;
            var name = extra ? ExtraMedia.Filename(media, file, file.Extra ?? ExtraMedia.Identify(media.Folder, file.Path)!) : template.Replace("{Title}", title).Replace("{Year}", m!.Year > 0 ? m.Year.ToString() : "").Replace("{Season}", file.Season.ToString("00")).Replace("{Episode}", file.Episode.ToString("00")).Replace("{EpisodeTitle}", resolved?.Episode.Title ?? "");
            name = SafeName(name);
            if (name.Length == 0) throw new ArgumentException(L.Text("Empty filename."));
            var folder = Path.GetDirectoryName(file.Path)!;
            var organize = !extra && media.Kind == LibraryMediaKind.Series && file.Episode > 0 && (file.Season > 0 || organizeSpecials && file.Season == 0);
            var destinationFolder = extra ? ExtraMedia.DestinationFolder(media.Folder, file.Path, profile ?? MediaOutputProfile.Jellyfin) : organize ? (profile ?? MediaOutputProfile.Jellyfin).EpisodeFolder(media, file, organizeSpecials) : folder;
            if (organize && !Path.GetFullPath(file.Path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(media.Folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.Text("Episodes must remain inside the series folder."));
            var target = Path.Combine(destinationFolder, name + Path.GetExtension(file.Path));
            Add(file.Path, target);
            var oldBase = Path.GetFileNameWithoutExtension(file.Path);
            foreach (var sidecar in (folderFiles?.GetValueOrDefault(folder) ?? Directory.GetFiles(folder)).Where(p => !LibraryScanner.IsVideo(p)))
            {
                var tail = Path.GetFileName(sidecar);
                if (!tail.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)) continue;
                tail = tail[oldBase.Length..];
                // Exact NFO, language/flag subtitles, and explicitly named artwork only. A decimal episode is never a companion.
                var subtitle = Regex.IsMatch(tail, @"^(?:\.[A-Za-z][A-Za-z0-9_-]*)*\.(srt|ass|ssa|sub|idx|sup|vtt)$", RegexOptions.IgnoreCase);
                var asset = Regex.IsMatch(tail, @"^\.nfo$|^-(thumb|poster|fanart|backdrop)\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase);
                if (subtitle || asset) Add(sidecar, Path.Combine(destinationFolder, name + tail), organize && tail.Equals(".nfo", StringComparison.OrdinalIgnoreCase) ? file : null);
            }
        }
        if (validate) Validate(plan);
        return plan;
        void Add(string from, string to, LocalMediaFile? numbering = null) { if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) plan.Add(new(from, to) { OutputSeason = numbering?.Season, OutputEpisode = numbering?.Episode }); }
    }
    public List<RenamePreviewGroup> BuildPreview(LibraryMedia media, string template, MediaOutputProfile? profile = null, bool organizeSpecials = true)
    {
        var groups = new List<RenamePreviewGroup>();
        var snapshots = media.Files.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, f => new DirectoryInfo(f).EnumerateFileSystemInfos().ToArray(), StringComparer.OrdinalIgnoreCase);
        var entries = snapshots.ToDictionary(p => p.Key, p => p.Value.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)).Select(f => f.FullName).ToArray(), StringComparer.OrdinalIgnoreCase);
        var existing = snapshots.Values.SelectMany(e => e.Select(f => f.FullName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = entries.Values.SelectMany(e => e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in media.Files)
        {
            var key = media.Kind == LibraryMediaKind.Movie ? -1 : file.Season;
            var extra = IsOrganizableExtra(media, file);
            if (file.Exclusion.Length > 0 && !extra) { groups.Add(new() { File = file, State = RenamePreviewState.Excluded, Detail = file.Exclusion }); continue; }
            if (!extra && (media.Kind == LibraryMediaKind.Series ? EpisodeMatching.Resolve(media, file) == null : !media.Matches.ContainsKey(key)))
            { groups.Add(new() { File = file, State = RenamePreviewState.Unmatched, Detail = L.Text("No confirmed episode match") }); continue; }
            var operations = Preview(new LibraryMedia { Folder = media.Folder, Title = media.Title, Kind = media.Kind, Matches = media.Matches, Documents = media.Documents, EpisodeBindings = media.EpisodeBindings, Edits = media.Edits, Files = new() { file } }, template, false, entries, profile, organizeSpecials);
            var video = operations.FirstOrDefault(p => p.Source == file.Path);
            var oldBase = Path.GetFileNameWithoutExtension(file.Path);
            var related = entries[Path.GetDirectoryName(file.Path)!].Where(p =>
            {
                var name = Path.GetFileName(p); if (!name.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)) return false;
                var tail = name[oldBase.Length..];
                return Regex.IsMatch(tail, @"^(?:\.[A-Za-z][A-Za-z0-9_-]*)*\.(srt|ass|ssa|sub|idx|sup|vtt)$|^\.nfo$|^-(thumb|poster|fanart|backdrop)\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase);
            }).ToList();
            if (media.Kind == LibraryMediaKind.Movie && media.Files.Count(f => f.Exclusion.Length == 0) == 1)
                related.AddRange(entries.GetValueOrDefault(media.Folder, Array.Empty<string>()).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^(movie\.nfo|poster\.(jpg|jpeg|png|webp)|fanart\.(jpg|jpeg|png|webp)|backdrop\.(jpg|jpeg|png|webp)|clearlogo\.png)$", RegexOptions.IgnoreCase)));
            groups.Add(new() { File = file, IsExtra = extra, Detail = extra ? L.Text("Organize as extras; no episode number or metadata match is assigned.") : "", ProposedName = video == null ? file.Name : Path.GetFileName(video.Destination), State = operations.Count == 0 ? RenamePreviewState.Unchanged : RenamePreviewState.Changed, Selected = operations.Count > 0, Operations = operations, RelatedFiles = related.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        }
        var repeatedTargets = groups.SelectMany(g => g.Operations).GroupBy(p => p.Destination, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateEpisodes = media.Kind == LibraryMediaKind.Series ? media.Files.Where(f => f.Exclusion.Length == 0 && !IsOrganizableExtra(media, f)).GroupBy(f => (f.Season, f.Episode)).Where(g => g.Count() > 1).SelectMany(g => g).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        foreach (var group in groups.Where(g => g.State is RenamePreviewState.Changed or RenamePreviewState.Unchanged))
        {
            var conflict = group.Operations.FirstOrDefault(p => repeatedTargets.Contains(p.Destination) || !sourceFiles.Contains(p.Source) || existing.Contains(p.Destination) || File.Exists(p.Destination) || Directory.Exists(p.Destination) || InvalidDestinationParent(Path.GetDirectoryName(p.Destination)!));
            if (conflict != null || duplicateEpisodes.Contains(group.File.Path))
            { group.State = RenamePreviewState.Conflict; group.Selected = false; group.Detail = duplicateEpisodes.Contains(group.File.Path) ? L.Text("Duplicate episode mapping") : L.Text("Target exists, duplicate target or source missing") + ": " + Path.GetFileName(conflict!.Destination); }
        }
        return groups;
    }
    public static string SafeName(string name) => Utilities.WindowsFileName.Sanitize(name);
    public static void Validate(IReadOnlyList<FileRename> plan, FolderRename? folder = null)
    {
        if (folder != null)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Source));
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Destination));
            if (!Directory.Exists(source) || Directory.Exists(destination) || File.Exists(destination) || InvalidDestinationParent(source) ||
                !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase) ||
                plan.Any(p => !p.Source.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !p.Destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(L.Format($"Rename conflict or missing file: {folder.Destination}"));
        }
        if (plan.Select(p => p.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count) throw new IOException(L.Text("Rename destinations conflict."));
        foreach (var p in plan)
            if (!File.Exists(p.Source) || File.Exists(p.Destination) || Directory.Exists(p.Destination) || InvalidDestinationParent(Path.GetDirectoryName(p.Destination)!)) throw new IOException(L.Format($"Rename conflict or missing file: {p.Destination}"));
    }
    private static bool InvalidDestinationParent(string folder)
    {
        for (var current = Path.GetFullPath(folder); current != null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current)) return true;
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }
    public void Execute(IReadOnlyList<FileRename> plan, FolderRename? folder = null)
    {
        Validate(plan, folder);
        if (plan.Count == 0 && folder == null) return;
        var history = new RenameHistoryStore(historyDirectory); var journal = history.Begin(plan, folder);
        var done = new List<FileRename>();
        var folderMoved = false;
        var createdFolders = new List<string>();
        var originalNfos = new Dictionary<string, byte[]>();
        try
        {
            foreach (var p in plan)
            {
                var missing = new Stack<string>();
                for (var parent = Path.GetDirectoryName(Path.GetFullPath(p.Destination)); parent != null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)) missing.Push(parent);
                while (missing.TryPop(out var parent)) { Directory.CreateDirectory(parent); createdFolders.Add(parent); }
                if (InvalidDestinationParent(Path.GetDirectoryName(p.Destination)!)) throw new IOException(L.Format($"Rename conflict or missing file: {p.Destination}"));
                File.Move(p.Source, p.Destination, false); done.Add(p);
            }
            foreach (var p in plan.Where(p => Path.GetExtension(p.Destination).Equals(".nfo", StringComparison.OrdinalIgnoreCase)))
            {
                var xml = XDocument.Load(p.Destination); var changed = false;
                if (p.OutputSeason is int season && p.OutputEpisode is int episode)
                {
                    if (xml.Root?.Name != "episodedetails") throw new InvalidDataException(L.Text("Existing NFO type differs from the selected media type."));
                    if (xml.Root.Element("season")?.Value != season.ToString(CultureInfo.InvariantCulture) || xml.Root.Element("episode")?.Value != episode.ToString(CultureInfo.InvariantCulture)) { xml.Root.SetElementValue("season", season); xml.Root.SetElementValue("episode", episode); changed = true; }
                }
                foreach (var element in xml.Descendants().Where(e => !e.HasElements && e.Name.LocalName is "thumb" or "poster" or "fanart"))
                {
                    if (Uri.TryCreate(element.Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") continue;
                    if (string.IsNullOrWhiteSpace(element.Value)) continue;
                    var reference = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p.Source)!, element.Value));
                    var moved = plan.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Source), reference, StringComparison.OrdinalIgnoreCase));
                    var differentFolder = !string.Equals(Path.GetDirectoryName(p.Source), Path.GetDirectoryName(p.Destination), StringComparison.OrdinalIgnoreCase);
                    if (moved != null || differentFolder && !Path.IsPathRooted(element.Value) && File.Exists(reference))
                    {
                        var replacement = Path.GetRelativePath(Path.GetDirectoryName(p.Destination)!, moved?.Destination ?? reference).Replace('\\', '/');
                        if (element.Value != replacement) { element.Value = replacement; changed = true; }
                    }
                }
                if (!changed) continue;
                originalNfos[p.Destination] = File.ReadAllBytes(p.Destination);
                LibraryOutputService.WriteNfo(p.Destination, xml.Root!, true);
            }
            if (folder != null)
            {
                // Keep absolute local NFO references valid as well as relative references.
                foreach (var path in Directory.EnumerateFiles(folder.Source, "*.nfo", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var xml = XDocument.Load(path); var changed = false;
                    foreach (var element in xml.Descendants().Where(e => !e.HasElements && e.Name.LocalName is "thumb" or "poster" or "fanart"))
                    {
                        if (!Path.IsPathRooted(element.Value)) continue;
                        var replacement = LibraryPathMigration.MoveRoot(element.Value, folder);
                        if (replacement != element.Value) { element.Value = replacement; changed = true; }
                    }
                    if (!changed) continue;
                    originalNfos.TryAdd(path, File.ReadAllBytes(path));
                    LibraryOutputService.WriteNfo(path, xml.Root!, true);
                }
                Directory.Move(folder.Source, folder.Destination); folderMoved = true;
            }
            history.Finish(journal, "Completed");
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (folderMoved) try { Directory.Move(folder!.Destination, folder.Source); } catch (Exception rollback) { failures.Add(rollback); }
            foreach (var nfo in originalNfos) try { File.WriteAllBytes(nfo.Key, nfo.Value); } catch (Exception restore) { failures.Add(restore); }
            foreach (var p in done.AsEnumerable().Reverse())
                try { File.Move(p.Destination, p.Source, false); } catch (Exception rollback) { failures.Add(rollback); }
            foreach (var created in createdFolders.AsEnumerable().Reverse())
                try { if (!Directory.EnumerateFileSystemEntries(created).Any()) Directory.Delete(created, false); } catch (Exception rollback) { failures.Add(rollback); }
            try { history.Finish(journal, failures.Count == 1 ? "RolledBack" : "RollbackIncomplete"); } catch (Exception logging) { failures.Add(logging); }
            throw new AggregateException(L.Text("Rename failed; rollback attempted. Check the listed paths."), failures);
        }
    }
}
