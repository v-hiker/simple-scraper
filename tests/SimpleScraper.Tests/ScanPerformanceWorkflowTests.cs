using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class ScanPerformanceWorkflowTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var fs = new ScanTestFileSystem();
        var root = Path.Combine(temporary, "virtual-library");
        var show = Path.Combine(root, "Z Series (2020)");
        var movie = Path.Combine(root, "A Movie (2021)");
        var empty = Path.Combine(root, "M Empty");
        var season = Path.Combine(show, "Season 1");
        var specials = Path.Combine(show, "Specials");
        var oped = Path.Combine(show, "OPED");
        var nestedExtras = Path.Combine(season, "extras");
        fs.Add(root, Array.Empty<string>(), show, movie, empty);
        fs.Add(show, new[] { "tvshow.nfo", "unused.nfo", "poster.png", "fanart.webp", "logo.jpg", "title-clearlogo.webp", "season01-banner.jpg" }, season, specials, oped);
        fs.Add(season, new[] { "S01E01.mkv", "S01E01.nfo", "S01E01-thumb.webp", "mystery.mkv", "mystery.nfo", "S01E11.5.mkv", "S01E11.5.nfo", "season.nfo", "unused.nfo", "folder.jpg", "logo.png" }, nestedExtras);
        fs.Add(specials, new[] { "S00E01.mkv", "season.nfo", "poster.webp" });
        fs.Add(oped, new[] { "NCOP01.mkv" });
        fs.Add(nestedExtras, new[] { "trailer.mkv" });
        fs.Add(movie, new[] { "original.mkv", "original.nfo", "movie.nfo", "unused.nfo", "poster.jpg" });
        fs.Add(empty, Array.Empty<string>());
        fs.Nfo(show, "tvshow.nfo", "<tvshow><title>Local show</title><uniqueid type='tmdb'>10</uniqueid><rating>8.2</rating></tvshow>");
        fs.Nfo(season, "S01E01.nfo", "<episodedetails><title>First</title><season>1</season><episode>1</episode><rating>9.1</rating></episodedetails>");
        fs.Nfo(season, "mystery.nfo", "<episodedetails><title>Fallback</title><season>1</season><episode>2</episode></episodedetails>");
        fs.Nfo(season, "S01E11.5.nfo", "<episodedetails><season>1</season><episode>11</episode></episodedetails>");
        fs.Nfo(season, "season.nfo", "<season><title>Season one</title><rating>7.8</rating></season>");
        fs.Nfo(specials, "season.nfo", "<season><title>Specials</title></season>");
        fs.Nfo(movie, "original.nfo", "<movie><title>Companion movie</title><uniqueid type='tmdb'>20</uniqueid></movie>");
        fs.Nfo(movie, "movie.nfo", "<movie><title>Generic movie</title></movie>");
        // Auto movie detection still checks other root NFOs for a TV title,
        // which takes precedence. A TV title and its numbered season do not
        // need the unrelated sidecars, deliberately left unreadable here.
        fs.Nfo(movie, "unused.nfo", "<note />");
        var scanner = new LibraryScanner(fs);
        var progress = new ProgressRecorder();
        var result = scanner.Scan(new("Virtual", root, LibraryMediaKind.Auto, true) { AutoLayout = true }, progress: progress);
        check(result.Select(m => m.Folder).SequenceEqual(new[] { movie, show }), "concurrent scan returns sorted titles and omits empty folders");
        check(fs.DirectoryReads.Values.All(n => n == 1) && fs.DirectoryReads.Count == 8, "layout detection, seasons, extras and artwork share one listing per visited directory");
        check(fs.DirectoryChecks == 1 && fs.OpenedNfos.Values.All(n => n == 1), "scan probes only its root directory and opens each needed NFO once");
        check(!fs.OpenedNfos.ContainsKey(Path.Combine(show, "unused.nfo")) && !fs.OpenedNfos.ContainsKey(Path.Combine(season, "unused.nfo")), "established title and episode hints avoid reading unrelated series NFO files");
        check(progress.Items.Select(p => p.Completed).SequenceEqual(new[] { 0, 1, 2, 3 }) && progress.Items.All(p => p.Total == 3), "parallel progress is monotonic and counts empty folders too");
        var media = result[1];
        check(media.Kind == LibraryMediaKind.Series && media.Title == "Z Series" && media.LocalMetadata?.Title == "Local show" && media.LocalMetadata?.Rating == 8.2 && media.ExistingIds["tmdb"] == "10", "scan preserves folder title, local metadata title, score and identifiers");
        var unknown = media.Files.Single(f => f.Name == "mystery.mkv");
        check(unknown.Season == 1 && unknown.Episode == 2 && !unknown.NeedsReview && unknown.LocalMetadata?.Title == "Fallback", "unnumbered files retain episode NFO numbering fallback");
        var fractional = media.Files.Single(f => f.Name == "S01E11.5.mkv");
        check(fractional.NeedsReview && fractional.SourceNumber == "11.5" && fractional.Episode == 0, "NFO fallback preserves fractional numbering for manual review");
        check(media.Files.Single(f => f.Name == "S00E01.mkv").Season == 0 && media.Files.Count(f => f.Exclusion.Length > 0) == 3, "specials and both root and nested extras retain scan classification");
        check(media.Files.Single(f => f.Name == "S01E01.mkv").ThumbPath == Path.Combine(season, "S01E01-thumb.webp") && media.LocalSeasons[1].Rating == 7.8 && media.SeasonNfoPaths.ContainsKey(0), "episode thumbnails and independent season NFOs come from inventories");
        check(media.ArtworkPaths["clearlogo"] == Path.Combine(show, "title-clearlogo.webp") && media.ArtworkPaths["poster"] == Path.Combine(show, "poster.png"), "work artwork preserves clearlogo preference and wildcard priority");
        check(media.SeasonArtworkPaths[1]["poster"] == Path.Combine(season, "folder.jpg") && media.SeasonArtworkPaths[1]["clearlogo"] == Path.Combine(season, "logo.png") && media.SeasonArtworkPaths[1]["banner"] == Path.Combine(show, "season01-banner.jpg"), "season artwork retains folder, logo and root season filename fallbacks");
        check(result[0].LocalMetadata?.Title == "Companion movie" && result[0].NfoPath == Path.Combine(movie, "original.nfo"), "movie companion NFO still wins over generic movie.nfo");

        fs.Add(season, fs.Listings[season].Files.Select(Path.GetFileName).Append("S01E03.mkv").OfType<string>(), nestedExtras);
        var refreshed = scanner.Scan(show, LibraryMediaKind.Series, false).Single();
        check(refreshed.Files.Any(f => f.Episode == 3) && fs.DirectoryReads[show] == 2 && fs.DirectoryReads[season] == 2, "refresh builds fresh inventories and sees newly added files");

        var orphanRoot = Path.Combine(temporary, "orphan-numbering");
        fs.Add(orphanRoot, new[] { "unnumbered.mkv", "catalog.nfo" });
        fs.Nfo(orphanRoot, "catalog.nfo", "<episodedetails><season>2</season><episode>9</episode></episodedetails>");
        check(scanner.Scan(orphanRoot, LibraryMediaKind.Auto, false).Single().Kind == LibraryMediaKind.Series, "orphan episode NFO still identifies a series when filename hints are absent");
        var moviePriority = Path.Combine(temporary, "movie-nfo-priority"); var misleadingSeason = Path.Combine(moviePriority, "Season 1");
        fs.Add(moviePriority, new[] { "original.mkv", "movie.nfo" }, misleadingSeason); fs.Add(misleadingSeason, new[] { "S01E01.mkv" });
        fs.Nfo(moviePriority, "movie.nfo", "<movie><title>Explicit movie</title></movie>");
        var priority = scanner.Scan(moviePriority, LibraryMediaKind.Auto, false).Single();
        check(priority.Kind == LibraryMediaKind.Movie && priority.Files.Count == 1, "movie NFO retains precedence over misleading season folders");
        var brokenRoot = Path.Combine(temporary, "invalid-nfo");
        fs.Add(brokenRoot, new[] { "original.mkv", "movie.nfo" }); fs.Nfo(brokenRoot, "movie.nfo", "<!DOCTYPE movie [<!ENTITY x SYSTEM 'file:///unread'>]><movie><title>&x;</title></movie>");
        check(scanner.Scan(brokenRoot, LibraryMediaKind.Movie, false).Single().ScanWarning.Length > 0, "secure lazy NFO reader rejects DTD without preventing media loading");

        var missing = false; try { scanner.Scan(Path.Combine(temporary, "missing-virtual"), LibraryMediaKind.Auto, false); } catch (DirectoryNotFoundException) { missing = true; }
        check(missing, "missing scan root still raises DirectoryNotFoundException");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var readsBefore = fs.DirectoryReads.Values.Sum();
        var wasCancelled = false; try { scanner.Scan(root, LibraryMediaKind.Auto, true, cancelled.Token); } catch (OperationCanceledException) { wasCancelled = true; }
        check(wasCancelled && fs.DirectoryReads.Values.Sum() == readsBefore, "pre-cancelled scans perform no directory reads");
        BoundedConcurrency(temporary, check);
        ArtworkParity(temporary, check);
        EpisodeIndexParity(check);
    }

    private static void BoundedConcurrency(string temporary, Action<bool, string> check)
    {
        var root = Path.Combine(temporary, "virtual-parallel"); var folders = Enumerable.Range(0, 12).Select(i => Path.Combine(root, $"Work {i:00}")).Reverse().ToArray();
        ScanTestFileSystem Fixture()
        {
            var fs = new ScanTestFileSystem { DirectoryDelayMs = 25 };
            fs.Add(root, Array.Empty<string>(), folders);
            foreach (var folder in folders) fs.Add(folder, new[] { "original.mkv" });
            return fs;
        }
        var serialFs = Fixture(); var timer = Stopwatch.StartNew(); var serial = new LibraryScanner(serialFs, 1).Scan(root, LibraryMediaKind.Movie, true); var serialMs = timer.Elapsed.TotalMilliseconds;
        var parallelFs = Fixture(); timer.Restart(); var parallel = new LibraryScanner(parallelFs, 4).Scan(root, LibraryMediaKind.Movie, true); var parallelMs = timer.Elapsed.TotalMilliseconds;
        check(parallelFs.MaximumConcurrentReads is > 1 and <= 4 && serialFs.MaximumConcurrentReads == 1, "title scanning overlaps read latency with at most four concurrent readers");
        check(JsonSerializer.Serialize(serial) == JsonSerializer.Serialize(parallel), "serial and concurrent scans produce the same complete result");
        Console.WriteLine($"SCAN synthetic read latency: serial={serialMs:0.0}ms; parallel={parallelMs:0.0}ms; listings={parallelFs.DirectoryReads.Values.Sum()}; max concurrency={parallelFs.MaximumConcurrentReads}.");
        using var cancellation = new CancellationTokenSource(); var cancelledFs = Fixture();
        cancelledFs.BeforeRead = path => { if (path != root) cancellation.Cancel(); };
        var cancelled = false; try { new LibraryScanner(cancelledFs, 4).Scan(root, LibraryMediaKind.Movie, true, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && cancelledFs.DirectoryReads.Count <= 5, "cancellation stops new titles while bounded in-flight readers unwind");
    }

    private static void ArtworkParity(string temporary, Action<bool, string> check)
    {
        var folder = Path.Combine(temporary, "inventory-artwork"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "season01-poster.jpg", "season-specials-clearlogo.jpg", "movie-poster.png", "poster.webp", "logo.jpg", "movie-clearlogo.webp", "movie-fanart.jpeg", "season1-banner.png" }) File.WriteAllText(Path.Combine(folder, name), "isolated image");
        var inventory = new PhysicalLibraryScanFileSystem().ReadDirectory(folder, default);
        foreach (var aspect in new[] { "poster", "fanart", "clearlogo", "backdrop", "banner" })
            check(LibraryAssets.FindArtwork(folder, aspect) == LibraryAssets.FindArtwork(inventory, aspect), "inventory artwork lookup matches physical priority for " + aspect);
        check(LibraryAssets.FindNamedArtwork(folder, "season1-banner") == LibraryAssets.FindNamedArtwork(inventory, "season1-banner"), "named artwork inventory matches physical lookup");
    }

    private static void EpisodeIndexParity(Action<bool, string> check)
    {
        MetadataEpisode Episode(string id, int number, double? score = null) => new(id, 1, number, id, "", "", 24, "") { Rating = score, VoteCount = 11 };
        var source = new MetadataDocument { Provider = "TMDB", Id = "1", Episodes = new() { Episode("first", 1, 9.2), Episode("second", 2), Episode("duplicate", 2, 7.1), Episode("third-duplicate", 2), Episode("same-id", 3), Episode("same-id", 4, 8.1) }, Seasons = new() { new(1, "Source season", "", "", "") { Rating = 7.5 } } };
        var media = new LibraryMedia { Kind = LibraryMediaKind.Series }; var key = EpisodeMatching.Key(source, media.Kind);
        media.Documents[key] = source; media.Matches[1] = new(new("TMDB", "1", "Source", "", 2020, "", ""), source);
        var files = Enumerable.Range(1, 6).Select(number => new LocalMediaFile { Path = "episode" + number + ".mkv", Season = 1, Episode = number }).ToList();
        media.Files = files;
        media.EpisodeBindings[files[2].Path] = new(key, source.Episodes[0] with { Rating = null, VoteCount = 0 }, 1, 1);
        media.EpisodeBindings[files[3].Path] = new(key, source.Episodes[4], 1, 3);
        media.EpisodeBindings[files[4].Path] = new("missing-document", source.Episodes[0], 1, 1);
        media.EpisodeBindings[files[5].Path] = new(key, source.Episodes[0], 1, 1); files[5].Exclusion = "extra";
        media.Edits.Episodes[files[0].Path] = new() { Title = "Manual title", Rating = 8.5 };
        var resolver = EpisodeMatching.CreateResolver(media);
        foreach (var file in files) check(EpisodeMatching.Resolve(media, file) == resolver.Resolve(file), "indexed resolution preserves direct business rules for " + file.Path);
        check(resolver.Resolve(files[1]) == null && resolver.Resolve(files[2])?.Episode.Rating == 9.2 && resolver.Resolve(files[3])?.Episode.Rating == null, "indexed ambiguity, binding rating refill and first-id behavior remain unchanged");
        check(ReferenceEquals(resolver.Resolve(files[2]), resolver.Resolve(files[2])), "projection resolver reuses already resolved file choices");
        foreach (var file in files) check(JsonSerializer.Serialize(MetadataEditing.Episode(media, file)) == JsonSerializer.Serialize(MetadataEditing.Episode(media, file, resolver.Resolve(file))), "pre-resolved episode metadata matches normal editing API for " + file.Path);
        check(JsonSerializer.Serialize(MetadataEditing.Season(media, 1)) == JsonSerializer.Serialize(MetadataEditing.Season(media, 1, files.Select(resolver.Resolve).OfType<EpisodeChoice>())), "pre-resolved season metadata matches normal editing API");
    }

    private sealed class ProgressRecorder : IProgress<LibraryScanProgress>
    {
        public List<LibraryScanProgress> Items { get; } = new();
        public void Report(LibraryScanProgress value) { lock (Items) Items.Add(value); }
    }

    private sealed class ScanTestFileSystem : ILibraryScanFileSystem
    {
        public Dictionary<string, LibraryDirectoryInventory> Listings { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> nfos = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> DirectoryReads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> OpenedNfos { get; } = new(StringComparer.OrdinalIgnoreCase);
        private int directoryChecks, activeReads, maximumConcurrentReads;
        public int DirectoryChecks => directoryChecks;
        public int MaximumConcurrentReads => maximumConcurrentReads;
        public int DirectoryDelayMs { get; init; }
        public Action<string>? BeforeRead { get; set; }
        public void Add(string path, IEnumerable<string> files, params string[] directories) => Listings[path] = new(path, files.Select(f => Path.Combine(path, f)), directories);
        public void Nfo(string folder, string name, string text) => nfos[Path.Combine(folder, name)] = text;
        public bool DirectoryExists(string path) { Interlocked.Increment(ref directoryChecks); return Listings.ContainsKey(path); }
        public LibraryDirectoryInventory ReadDirectory(string path, CancellationToken cancellation)
        {
            DirectoryReads.AddOrUpdate(path, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref activeReads);
            int maximum;
            do { maximum = maximumConcurrentReads; } while (active > maximum && Interlocked.CompareExchange(ref maximumConcurrentReads, active, maximum) != maximum);
            try
            {
                BeforeRead?.Invoke(path);
                if (DirectoryDelayMs > 0 && cancellation.WaitHandle.WaitOne(DirectoryDelayMs)) cancellation.ThrowIfCancellationRequested();
                cancellation.ThrowIfCancellationRequested();
                return Listings.TryGetValue(path, out var inventory) ? inventory : throw new DirectoryNotFoundException(path);
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }
        public Stream OpenRead(string path)
        {
            OpenedNfos.AddOrUpdate(path, 1, (_, count) => count + 1);
            return nfos.TryGetValue(path, out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false) : throw new IOException("Unneeded NFO must not be opened: " + path);
        }
    }
}
