using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Utilities;

namespace SimpleScraper.Services;

public sealed record LibraryScanProgress(int Completed, int Total, string Folder);
public sealed class LibraryScanner
{
    private readonly ILibraryScanFileSystem fileSystem;
    private readonly int maxConcurrency;
    public LibraryScanner() : this(new PhysicalLibraryScanFileSystem()) { }
    public LibraryScanner(ILibraryScanFileSystem fileSystem, int maxConcurrency = 4)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        if (maxConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        this.maxConcurrency = maxConcurrency;
    }
    public static bool IsVideo(string file) => FileFormatValidator.IsVideoFile(file);
    public static int? SeasonFolderNumber(string folder) => MediaRecognition.SeasonFolder(folder);

    public static bool DetectLibraryRoot(string path, CancellationToken cancellation = default)
        => DetectLibraryRoot(path, new ScanSession(new PhysicalLibraryScanFileSystem(), cancellation), cancellation);

    private static bool DetectLibraryRoot(string path, ScanSession session, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var inventory = session.Directory(path);
        var files = inventory.Files;
        // Media or a title NFO at the chosen level belongs to this title, even
        // when it also contains seasons, disc folders, subtitles or extras.
        if (files.Any(IsVideo) || files.Any(f => Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("movie.nfo", StringComparison.OrdinalIgnoreCase))) return false;
        if (files.Where(IsNfo).Any(f => session.Nfo(f)?.Name.LocalName is "movie" or "tvshow")) return false;
        if (SeasonFolderNumber(path) != null) return false;
        var children = inventory.Directories.Where(p => !Path.GetFileName(p).StartsWith('.') && !ExtraMedia.IsExtraFolder(p)).ToList();
        // An independently described title takes precedence over folder names.
        foreach (var child in children)
        {
            cancellation.ThrowIfCancellationRequested();
            var childInventory = session.Directory(child);
            if (childInventory.ExistingFile("tvshow.nfo") != null || childInventory.ExistingFile("movie.nfo") != null) return true;
        }
        if (children.Any(p => SeasonFolderNumber(p) != null)) return false;
        // A folder containing only extras is still one title, not a library
        // whose openings/endings should each become separate works.
        return children.Count > 0 || !inventory.Directories.Any(ExtraMedia.IsExtraFolder);
    }

    public List<LibraryMedia> Scan(MediaLibrary library, CancellationToken cancellation = default, IProgress<LibraryScanProgress>? progress = null, IReadOnlyDictionary<string, LibraryMediaKind>? overrides = null)
    {
        var session = new ScanSession(fileSystem, cancellation);
        var isRoot = library.AutoLayout ? DetectLibraryRoot(library.Path, session, cancellation) : library.IsRoot;
        return Scan(library.Path, library.Kind, isRoot, session, cancellation, progress, overrides);
    }

    public static void CorrectCachedAutoDetection(IEnumerable<LibraryMedia> media, bool detectMovies = true)
    {
        foreach (var item in media)
        {
            if (item.Kind == LibraryMediaKind.Movie && detectMovies && item.NfoPath == null &&
                (MediaRecognition.HasEpisodeNames(item.Files.Where(f => ExtraMedia.Identify(item.Folder, f.Path) == null).Select(f => f.Path)) || SeasonFolderNumber(item.Folder) != null)) SetMediaKind(item, LibraryMediaKind.Series);
            if (item.Kind != LibraryMediaKind.Series) continue;
            foreach (var file in item.Files.Where(f => f.NeedsReview && f.SourceNumber.Length == 0))
            {
                var hint = new LocalMediaFile { Path = file.Path }; ReadEpisodeNumber(item.Folder, hint);
                if (hint.NeedsReview || hint.Exclusion.Length > 0) continue;
                file.Season = hint.Season; file.Episode = hint.Episode; file.NeedsReview = false; file.Exclusion = "";
            }
        }
    }

    public static void SetMediaKind(LibraryMedia media, LibraryMediaKind kind)
    {
        media.Kind = kind;
        foreach (var file in media.Files)
        {
            file.Season = file.Episode = 0; file.NeedsReview = false; file.Exclusion = file.SourceNumber = "";
            if (kind == LibraryMediaKind.Series) ReadEpisodeNumber(media.Folder, file);
        }
    }

    private static bool IsNfo(string path) => Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase);

    private static void ReadEpisodeNumber(string folder, LocalMediaFile file)
    {
        var video = file.Path;
        var relative = Path.GetRelativePath(folder, video);
        var parsed = MediaRecognition.Parse(video);
        var season = SeasonFolderNumber(Path.GetDirectoryName(video) ?? "");
        file.Season = parsed?.Season ?? season ?? (parsed != null ? 1 : 0);
        if (Regex.IsMatch(relative, @"(?i)(^|[\\/])(OPED|extras|trailers|samples|备份字幕)([\\/]|$)") || parsed?.Extra == true) file.Exclusion = L.Text("Extra / opening / ending");
        else if (parsed?.NeedsReview == true)
        {
            file.NeedsReview = true; file.SourceNumber = parsed.SourceNumber;
            file.Exclusion = L.Text("Fractional or multi-episode number: manual mapping required");
        }
        else if (parsed is { Episode: > 0 }) file.Episode = parsed.Episode;
        else
        {
            var legacy = RegexPatterns.ParseTvEpisode(Path.GetFileName(video));
            if (legacy != null && (legacy.ShowName.Length > 0 || season != null))
            {
                file.Season = season ?? legacy.Season; file.Episode = legacy.Episode;
            }
            else { file.NeedsReview = true; file.Exclusion = L.Text("Episode number not recognized: manual mapping required"); }
        }
    }

    public List<LibraryMedia> Scan(string path, LibraryMediaKind kind, bool libraryRoot, CancellationToken cancellation = default, IProgress<LibraryScanProgress>? progress = null, IReadOnlyDictionary<string, LibraryMediaKind>? overrides = null)
        => Scan(path, kind, libraryRoot, new ScanSession(fileSystem, cancellation), cancellation, progress, overrides);

    private List<LibraryMedia> Scan(string path, LibraryMediaKind kind, bool libraryRoot, ScanSession session, CancellationToken cancellation, IProgress<LibraryScanProgress>? progress, IReadOnlyDictionary<string, LibraryMediaKind>? overrides)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!fileSystem.DirectoryExists(path)) throw new DirectoryNotFoundException(path);
        var folders = (libraryRoot ? session.Directory(path).Directories : new[] { path }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new LibraryMedia?[folders.Length];
        var progressLock = new object(); var done = 0;
        if (folders.Length > 0) progress?.Report(new(0, folders.Length, folders[0]));
        void ScanFolder(int index)
        {
            cancellation.ThrowIfCancellationRequested();
            var folder = folders[index];
            var chosenKind = overrides?.GetValueOrDefault(Path.GetFullPath(folder), kind) ?? kind;
            if (chosenKind is not (LibraryMediaKind.Movie or LibraryMediaKind.Series or LibraryMediaKind.Auto)) chosenKind = kind;
            var media = ScanOne(folder, chosenKind, libraryRoot ? session.ForkForTitle() : session, cancellation);
            if (media.Files.Count > 0) results[index] = media;
            // Serialize callbacks as well as the counter, even when titles finish
            // out of order. Returned titles retain their sorted folder order.
            lock (progressLock) progress?.Report(new(++done, folders.Length, folder));
        }
        if (folders.Length <= 1 || maxConcurrency == 1)
            for (var index = 0; index < folders.Length; index++) ScanFolder(index);
        else
        {
            try
            {
                Parallel.For(0, folders.Length, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellation }, ScanFolder);
            }
            catch (AggregateException error) when (error.InnerExceptions.Count == 1)
            { ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw(); throw; }
        }
        cancellation.ThrowIfCancellationRequested();
        return results.OfType<LibraryMedia>().ToList();
    }

    private LibraryMedia ScanOne(string folder, LibraryMediaKind kind, ScanSession session, CancellationToken cancellation)
    {
        folder = Path.GetFullPath(folder);
        var rootInventory = session.Directory(folder);
        var rootFiles = rootInventory.Files; var subfolders = rootInventory.Directories;
        var filesByFolder = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [folder] = rootFiles };
        void AddFolder(string sub)
        {
            if (!filesByFolder.ContainsKey(sub)) filesByFolder.Add(sub, session.Directory(sub).Files);
        }
        foreach (var sub in kind == LibraryMediaKind.Movie ? Array.Empty<string>() : subfolders)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(Path.GetFileName(sub), @"^(?:\.|extras$|trailers$|samples$|OPED$)", RegexOptions.IgnoreCase)) AddFolder(sub);
        }
        foreach (var sub in subfolders)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ExtraMedia.IsExtraFolder(sub)) AddFolder(sub);
            if (kind != LibraryMediaKind.Movie && SeasonFolderNumber(sub) is > 0)
                foreach (var extra in session.Directory(sub).Directories.Where(ExtraMedia.IsExtraFolder))
                { cancellation.ThrowIfCancellationRequested(); AddFolder(extra); }
        }
        if (kind == LibraryMediaKind.Auto)
        {
            var tvNfo = rootFiles.Any(f => Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))
                || rootFiles.Where(IsNfo).Any(f => session.Nfo(f)?.Name.LocalName == "tvshow");
            var movieNfo = !tvNfo && rootFiles.Where(IsNfo).Any(f => session.Nfo(f)?.Name.LocalName == "movie");
            var classificationFiles = filesByFolder.Values.SelectMany(f => f).Where(p => ExtraMedia.Identify(folder, p) == null).ToList();
            var episodeNames = MediaRecognition.HasEpisodeNames(classificationFiles);
            kind = tvNfo || !movieNfo && (SeasonFolderNumber(folder) != null || subfolders.Any(p => SeasonFolderNumber(p) != null) || episodeNames
                || classificationFiles.Where(IsNfo).Any(p => session.Nfo(p)?.Name.LocalName == "episodedetails")) ? LibraryMediaKind.Series : LibraryMediaKind.Movie;
        }
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var year = Regex.Match(name, @"\((?<y>(?:19|20)\d{2})\)");
        var media = new LibraryMedia { Folder = folder, Kind = kind, Title = Regex.Replace(name, @"\s*\((?:19|20)\d{2}\).*", "").Trim(), Year = year.Success ? int.Parse(year.Groups["y"].Value) : 0 };
        if (kind == LibraryMediaKind.Movie) filesByFolder = filesByFolder.Where(p => p.Key.Equals(folder, StringComparison.OrdinalIgnoreCase) || ExtraMedia.IsExtraFolder(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        else foreach (var sub in subfolders) { cancellation.ThrowIfCancellationRequested(); if (!Path.GetFileName(sub).StartsWith('.')) AddFolder(sub); }
        var allFiles = filesByFolder.Values.SelectMany(f => f).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videos = allFiles.Where(IsVideo).ToList();
        foreach (var video in videos.OrderBy(p => p))
        {
            cancellation.ThrowIfCancellationRequested();
            var file = new LocalMediaFile { Path = video, NfoPath = allFiles.Contains(Path.ChangeExtension(video, ".nfo")) ? Path.ChangeExtension(video, ".nfo") : null, ThumbPath = new[] { ".jpg", ".png", ".jpeg", ".webp" }.Select(ext => Path.ChangeExtension(video, null) + "-thumb" + ext).FirstOrDefault(allFiles.Contains) };
            var details = file.NfoPath == null ? null : session.Nfo(file.NfoPath);
            if (kind == LibraryMediaKind.Series)
            {
                ReadEpisodeNumber(folder, file);
                // NFO numbering is a fallback for unnumbered files; it must not
                // erase a fractional/multi-episode hint or an excluded extra.
                if (file.NeedsReview && file.SourceNumber.Length == 0 && details is { Name.LocalName: "episodedetails" } episodeRoot &&
                    int.TryParse(episodeRoot.Element("season")?.Value, out var localSeason) && localSeason is >= 0 and <= 999 &&
                    int.TryParse(episodeRoot.Element("episode")?.Value, out var localEpisode) && localEpisode is > 0 and <= 9999)
                { file.Season = localSeason; file.Episode = localEpisode; file.NeedsReview = false; file.Exclusion = ""; }
            }
            if (details is { Name.LocalName: "episodedetails" }) file.LocalMetadata = MetadataEditing.ReadNfo(details, Path.GetDirectoryName(file.Path)!);
            media.Files.Add(file);
        }
        ExtraMedia.ApplyHints(media);
        var nfo = kind == LibraryMediaKind.Series ? rootFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)) : rootFiles.FirstOrDefault(f => videos.Count > 0 && f.Equals(Path.ChangeExtension(videos[0], ".nfo"), StringComparison.OrdinalIgnoreCase)) ?? rootFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("movie.nfo", StringComparison.OrdinalIgnoreCase)) ?? rootFiles.FirstOrDefault(f => Path.GetExtension(f).Equals(".nfo", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(nfo))
        {
            media.NfoPath = nfo;
            var root = session.Nfo(nfo);
            if (root == null) media.ScanWarning = L.Text("Existing NFO contains invalid XML. Confirm the correct source and scrape to regenerate it.");
            if (root != null)
            {
                foreach (var id in root.Elements("uniqueid"))
                    if (id.Attribute("type") is { } type && id.Value.Length > 0) media.ExistingIds[type.Value.ToLowerInvariant()] = id.Value;
                foreach (var provider in new[] { "tmdb", "bangumi", "imdb", "tvdb" })
                    if (root.Element(provider + "id") is { } id && id.Value.Length > 0) media.ExistingIds[provider] = id.Value;
                media.LocalMetadata = new MetadataDocument { Title = root.Element("title")?.Value ?? media.Title, OriginalTitle = root.Element("originaltitle")?.Value ?? "", Overview = root.Element("plot")?.Value ?? "", Date = root.Element("premiered")?.Value ?? root.Element("releasedate")?.Value ?? "", Ids = new(media.ExistingIds), Genres = root.Elements("genre").Select(g => g.Value).ToList(), Cast = root.Elements("actor").Select(a => new CastMember { Name = a.Element("name")?.Value ?? "", Character = a.Element("role")?.Value ?? "" }).ToList() };
                media.LocalMetadata.CreditsLoaded = true;
                var sourceProvider = root.Element("scraper")?.Element("provider")?.Value ?? root.Elements("uniqueid").FirstOrDefault(e => e.Attribute("default")?.Value == "true")?.Attribute("type")?.Value ?? (media.ExistingIds.ContainsKey("tmdb") ? "tmdb" : media.ExistingIds.ContainsKey("bangumi") ? "bangumi" : "");
                media.LocalMetadata.Provider = sourceProvider.ToLowerInvariant() switch { "tmdb" => "TMDB", "bangumi" => "Bangumi", _ => sourceProvider };
                media.LocalMetadata.Id = root.Element("scraper")?.Element("id")?.Value ?? media.ExistingIds.GetValueOrDefault(sourceProvider.ToLowerInvariant(), "");
                media.LocalMetadata.Studios = root.Elements("studio").Select(e => e.Value).ToList();
                media.LocalMetadata.Crew = root.Elements("director").Select(e => new CrewMember { Name = e.Value, Job = "Director" }).Concat(root.Elements("credits").Select(e => new CrewMember { Name = e.Value, Job = "Writer" })).ToList();
                foreach (var pair in root.Elements("actor").Zip(media.LocalMetadata.Cast))
                {
                    var thumb = pair.First.Element("thumb")?.Value;
                    pair.Second.ProfilePath = string.IsNullOrWhiteSpace(thumb) ? null : Uri.TryCreate(thumb, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? thumb : Path.GetFullPath(Path.Combine(folder, thumb));
                    if (int.TryParse(pair.First.Element("order")?.Value, out var order)) pair.Second.Order = order;
                    if (int.TryParse(pair.First.Element(sourceProvider.ToLowerInvariant() + "id")?.Value, out var personId)) pair.Second.Id = personId;
                }
                media.LocalMetadata = MetadataEditing.ReadNfo(root, folder);
                if (media.LocalMetadata.Title.Length == 0) media.LocalMetadata.Title = media.Title;
            }
        }
        foreach (var aspect in new[] { "poster", "fanart", "backdrop", "clearlogo" })
        {
            var artwork = LibraryAssets.FindArtwork(rootInventory, aspect);
            if (artwork != null) media.ArtworkPaths[aspect] = artwork;
        }
        if (kind == LibraryMediaKind.Series)
        {
            foreach (var season in media.Files.Select(f => f.Season).Concat(filesByFolder.Keys.Select(SeasonFolderNumber).OfType<int>()).Distinct())
            {
                var seasonNfo = filesByFolder.Keys.Where(f => SeasonFolderNumber(f) == season).Select(f => Path.Combine(f, "season.nfo")).FirstOrDefault(allFiles.Contains);
                if (seasonNfo != null)
                {
                    media.SeasonNfoPaths[season] = seasonNfo;
                    if (session.Nfo(seasonNfo) is { Name.LocalName: "season" } seasonRoot) media.LocalSeasons[season] = MetadataEditing.ReadNfo(seasonRoot, Path.GetDirectoryName(seasonNfo)!);
                }
                var artwork = new Dictionary<string, string>();
                foreach (var aspect in new[] { "poster", "fanart", "backdrop", "banner", "clearlogo" })
                {
                    var stem = season == 0 ? "season-specials-" : $"season{season:00}-";
                    var path = filesByFolder.Keys.Where(f => SeasonFolderNumber(f) == season).Select(f => LibraryAssets.FindNamedArtwork(session.Directory(f), aspect) ?? (aspect == "poster" ? LibraryAssets.FindNamedArtwork(session.Directory(f), "folder") : aspect == "clearlogo" ? LibraryAssets.FindNamedArtwork(session.Directory(f), "logo") : null)).FirstOrDefault(p => p != null)
                        ?? LibraryAssets.FindNamedArtwork(rootInventory, stem + aspect) ?? LibraryAssets.FindNamedArtwork(rootInventory, $"season{season}-" + aspect);
                    if (path != null) artwork[aspect] = path;
                }
                if (artwork.Count > 0) media.SeasonArtworkPaths[season] = artwork;
            }
        }
        media.PosterPath = media.ArtworkPaths.GetValueOrDefault("poster");
        return media;
    }

    private sealed class ScanSession
    {
        private readonly ILibraryScanFileSystem fileSystem;
        private readonly CancellationToken cancellation;
        private readonly ConcurrentDictionary<string, Lazy<LibraryDirectoryInventory>> directories;
        private readonly ConcurrentDictionary<string, Lazy<XElement?>> nfos = new(StringComparer.OrdinalIgnoreCase);
        public ScanSession(ILibraryScanFileSystem fileSystem, CancellationToken cancellation)
            : this(fileSystem, cancellation, new(StringComparer.OrdinalIgnoreCase)) { }
        private ScanSession(ILibraryScanFileSystem fileSystem, CancellationToken cancellation, ConcurrentDictionary<string, Lazy<LibraryDirectoryInventory>> directories)
        { this.fileSystem = fileSystem; this.cancellation = cancellation; this.directories = directories; }
        // Share listings with layout detection, but release parsed episode XML
        // when each title finishes rather than retaining it for the whole library.
        public ScanSession ForkForTitle() => new(fileSystem, cancellation, directories);
        public LibraryDirectoryInventory Directory(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            return directories.GetOrAdd(fullPath, p => new(() => fileSystem.ReadDirectory(p, cancellation), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        public XElement? Nfo(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            return nfos.GetOrAdd(Path.GetFullPath(path), p => new(() => Read(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        private XElement? Read(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var stream = fileSystem.OpenRead(path);
                using var reader = System.Xml.XmlReader.Create(stream, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                return XDocument.Load(reader).Root;
            }
            catch (System.Xml.XmlException) { return null; }
        }
    }
}
