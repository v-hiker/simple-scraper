using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;
using SimpleScraper.Presentation;

var results = new Dictionary<string, object?>();
var checks = 0;
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new InvalidOperationException(description);
}

var allColumns = LibraryColumns.All.Select(c => new LibraryColumnPreference { Id = c.Id, Width = c.DefaultWidth, Visible = true }).ToList();
var defaultColumns = allColumns.Where(c => LibraryColumns.All.First(x => x.Id == c.Id).DefaultVisible).ToList();
Check(defaultColumns.Count == 9, "Nine default columns are benchmarked.");
var fixture = Fixtures.Cases();
var expanded = Fixtures.Expanded(fixture);
var treeStates = new[] { expanded, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(new[] { fixture[0].Folder, fixture[0].Folder + "|season:1" }, StringComparer.OrdinalIgnoreCase) };
foreach (var language in new[] { "en-US", "zh-CN" })
{
    L.Initialize(language);
    foreach (var media in fixture)
    foreach (var season in media.Files.Select(f => f.Season).Concat(new[] { 0, 1, 2, 3, 5 }).Where(s => s >= 0).Distinct())
        Check(JsonSerializer.Serialize(BaselineRows.Season(media, season)) == JsonSerializer.Serialize(MetadataEditing.Season(media, season)), "Default season API preserves the original metadata output.");
    foreach (var treeState in treeStates)
    foreach (var sort in allColumns.Select(c => c.Id))
    foreach (var descending in new[] { false, true })
    foreach (var state in Enumerable.Range(0, 5))
    foreach (var query in new[] { "", "Fixture", "missing-query" })
    {
        var baseline = BaselineRows.Build(fixture, allColumns, treeState, query, state, sort, descending);
        var current = LibraryRowProjection.Build(fixture, allColumns, treeState, query, state, sort, descending);
        Check(baseline.Count == current.Count, $"Row count changed for {language}/{sort}/{state}.");
        for (var row = 0; row < baseline.Count; row++)
        {
            var a = baseline[row]; var b = current[row];
            Check(a.Key == b.Key && a.Season == b.Season && a.Depth == b.Depth && a.Expanded == b.Expanded && ReferenceEquals(a.Media, b.Media) && ReferenceEquals(a.File, b.File), $"Row identity/structure changed for {language}/{sort}/{state}, row {row}.");
            Check(a.Values.SequenceEqual(b.Values), $"Cell output changed for {language}/{sort}/{state}, row {row}: " + string.Join(", ", a.Values.Zip(b.Values).Select((v, i) => (v, i)).Where(v => v.v.First != v.v.Second).Select(v => allColumns[v.i].Id)));
        }
    }
}
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    var rejected = false;
    try { LibraryRowProjection.Build(fixture, allColumns, expanded, "", 0, "title", false, canceled.Token); }
    catch (OperationCanceledException) { rejected = true; }
    Check(rejected, "Projection honors canceled work.");
}
if (args.Contains("--verify-only", StringComparer.Ordinal))
{
    Console.WriteLine($"Passed projection equivalence: {checks} assertions.");
    return;
}
L.Initialize("zh-CN");
var benchmarks = new List<object>();
foreach (var count in new[] { 500, 2000, 5000 })
{
    var series = Fixtures.Series(count, 1); var input = new[] { series }; var tree = Fixtures.Expanded(input);
    var old = MeasureSamples(() => BaselineRows.Build(input, defaultColumns, tree, "", 0, "title", false), x => x.Count);
    var current = MeasureSamples(() => LibraryRowProjection.Build(input, defaultColumns, tree, "", 0, "title", false), x => x.Count);
    var oldSeason = MeasureSamples(() => BaselineRows.Season(series, 1), _ => 1);
    var currentSeason = MeasureSamples(() => MetadataEditing.Season(series, 1), _ => 1);
    Check(old.Rows == current.Rows && old.Rows == count + 2, "Expanded synthetic row counts match.");
    benchmarks.Add(new { Episodes = count, Columns = defaultColumns.Count, Baseline = old, Projection = current, Speedup = old.Milliseconds / Math.Max(0.001, current.Milliseconds), AllocationRatio = old.AllocatedBytes / (double)Math.Max(1, current.AllocatedBytes), SeasonBaseline = oldSeason, SeasonDefaultCurrent = currentSeason });
}
results["Synthetic"] = benchmarks;
results["Assertions"] = checks;
results["Environment"] = new { Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Build = "Release", TimingPolicy = "synthetic: three full-size warmups and median of three synchronous samples per algorithm/size; cache: three synchronous samples after one warmup; elapsed time and current-thread allocations", Scope = "in-memory row values and tree structure; no XAML layout, disk media or network calls" };

var cacheArgument = Array.IndexOf(args, "--cache");
var stateArgument = Array.IndexOf(args, "--state");
if (cacheArgument >= 0 && stateArgument >= 0)
{
    var cachePath = Path.GetFullPath(args[cacheArgument + 1]); var statePath = Path.GetFullPath(args[stateArgument + 1]);
    var initialCacheHash = Hash(cachePath); var initialStateHash = Hash(statePath);
    var state = JsonSerializer.Deserialize<LibraryStateStore>(File.ReadAllText(statePath))!;
    var library = state.Libraries.First(l => LibrarySnapshotStore.Key(l) == Path.GetFileNameWithoutExtension(cachePath));
    var store = new LibrarySnapshotStore(Path.GetDirectoryName(cachePath));
    var snapshot = store.Load(library)!;
    var copy = LibrarySnapshotStore.CopyMedia(snapshot, library.Kind);
    LibrarySnapshotStore.ApplyRecords(copy, state);
    var savedColumns = state.Columns.Where(c => c.Visible).ToList();
    var savedTree = state.ExpandedRows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var savedBaseline = BaselineRows.Build(copy, savedColumns, savedTree, "", 0, state.SortColumn, state.SortDescending);
    var savedProjection = LibraryRowProjection.Build(copy, savedColumns, savedTree, "", 0, state.SortColumn, state.SortDescending);
    Check(savedBaseline.Count == savedProjection.Count, "Private saved tree row count matches.");
    for (var row = 0; row < savedBaseline.Count; row++) Check(savedBaseline[row].Key == savedProjection[row].Key && savedBaseline[row].Depth == savedProjection[row].Depth && savedBaseline[row].Expanded == savedProjection[row].Expanded && savedBaseline[row].Values.SequenceEqual(savedProjection[row].Values), "Private saved tree structure and values match.");
    var savedOld = MeasureSamples(() => BaselineRows.Build(copy, savedColumns, savedTree, "", 0, state.SortColumn, state.SortDescending), x => x.Count);
    var savedNew = MeasureSamples(() => LibraryRowProjection.Build(copy, savedColumns, savedTree, "", 0, state.SortColumn, state.SortDescending), x => x.Count);
    var samples = new List<CacheMeasurement>();
    for (var sample = 0; sample < 3; sample++)
    {
        LibrarySnapshot? loaded = null; List<LibraryMedia>? detached = null;
        var load = Measure(() => loaded = store.Load(library)!, x => x.Media.Count);
        var clone = Measure(() => detached = LibrarySnapshotStore.CopyMedia(loaded!, library.Kind), x => x.Count);
        var overlay = Measure(() => { LibrarySnapshotStore.ApplyRecords(detached!, state); return detached!; }, x => x.Count);
        var projected = Measure(() => LibraryRowProjection.Build(detached!, defaultColumns, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "", 0, "title", false), x => x.Count);
        samples.Add(new(sample + 1, load, clone, overlay, projected));
    }
    Check(initialCacheHash == Hash(cachePath) && initialStateHash == Hash(statePath), "Private cache and matching state stay unchanged.");
    results["PrivateSnapshot"] = new { CacheBytes = new FileInfo(cachePath).Length, StateBytes = new FileInfo(statePath).Length, Works = snapshot.Media.Count, Files = snapshot.Media.Sum(m => m.Files.Count), MatchedWorks = copy.Count(m => m.Matches.Count > 0 || m.EpisodeBindings.Count > 0), BoundFiles = copy.Sum(m => m.EpisodeBindings.Count), EditedWorks = copy.Count(m => m.Edits.Work != null), ReadOnlyHashVerified = true, SavedTree = new { Columns = savedColumns.Count, Baseline = savedOld, Projection = savedNew, Equivalent = true }, Medians = new { Load = Median(samples.Select(s => s.Load)), CopyMedia = Median(samples.Select(s => s.CopyMedia)), ApplyRecords = Median(samples.Select(s => s.ApplyRecords)), CollapsedProjection = Median(samples.Select(s => s.CollapsedProjection)) }, Samples = samples };
}
results["Assertions"] = checks;
var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
var outputArgument = Array.IndexOf(args, "--output");
if (outputArgument >= 0) File.WriteAllText(args[outputArgument + 1], json, new System.Text.UTF8Encoding(false));
Console.WriteLine(json);

static Measurement Measure<T>(Func<T> action, Func<T, int> rows)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); var value = action(); watch.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    return new(watch.Elapsed.TotalMilliseconds, allocated, rows(value));
}
static RepeatedMeasurement MeasureSamples<T>(Func<T> action, Func<T, int> rows)
{
    for (var i = 0; i < 3; i++) action();
    var samples = Enumerable.Range(0, 3).Select(_ => Measure(action, rows)).ToList();
    var median = samples.OrderBy(s => s.Milliseconds).ElementAt(1);
    return new(median.Milliseconds, median.AllocatedBytes, median.Rows, samples);
}
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
static Measurement Median(IEnumerable<Measurement> samples) => samples.OrderBy(s => s.Milliseconds).ElementAt(1);
internal sealed record Measurement(double Milliseconds, long AllocatedBytes, int Rows);
internal sealed record RepeatedMeasurement(double Milliseconds, long AllocatedBytes, int Rows, IReadOnlyList<Measurement> Samples);
internal sealed record CacheMeasurement(int Sample, Measurement Load, Measurement CopyMedia, Measurement ApplyRecords, Measurement CollapsedProjection);

internal static class Fixtures
{
    public static LibraryMedia Series(int count, int seasons)
    {
        var doc = new MetadataDocument { Provider = "TMDB", Id = "fixture-series", Title = "Fixture Series", OriginalTitle = "Original Fixture", Date = "2024-01-01", Rating = 7.5, Ids = new() { ["tmdb"] = "fixture-series" }, CreditsLoaded = true };
        var media = new LibraryMedia { Folder = "C:/synthetic/fixture-series", Title = "Fixture Series", Kind = LibraryMediaKind.Series, Year = 2023, NfoPath = "synthetic.nfo", PosterPath = "synthetic.jpg" };
        for (var i = 0; i < count; i++)
        {
            var season = i % seasons + 1; var number = i / seasons + 1;
            media.Files.Add(new() { Path = $"C:/synthetic/fixture-series/S{season:00}E{number:00000}.mkv", Season = season, Episode = number, NfoPath = i % 2 == 0 ? "synthetic.nfo" : null, ThumbPath = i % 3 == 0 ? "synthetic.jpg" : null });
            doc.Episodes.Add(new($"id-{i}", season, number, $"Episode {i}", "Synthetic overview", "2024-02-01", 24, "") { Rating = 6.5 + i % 3, VoteCount = i + 1 });
        }
        for (var season = 1; season <= seasons; season++)
        {
            doc.Seasons.Add(new(season, $"Remote season {season}", "Synthetic season overview", "2024-01-01", "") { Rating = 7 });
            media.Matches[season] = new(new(doc.Provider, doc.Id, doc.Title, doc.OriginalTitle, doc.Year, doc.Overview, ""), doc);
        }
        media.Documents[EpisodeMatching.Key(doc, media.Kind)] = doc;
        media.SeasonNfoPaths[1] = "synthetic.nfo"; media.SeasonArtworkPaths[1] = new() { ["poster"] = "synthetic.jpg" };
        return media;
    }
    public static List<LibraryMedia> Cases()
    {
        var media = Series(20, 2); var doc = media.Matches[1].Document;
        media.Files[0].Exclusion = "Manually excluded"; media.Files[0].ManuallyExcluded = true;
        media.Files[1].NeedsReview = true; media.Files[1].Exclusion = "Fractional episode"; media.Files[1].SourceNumber = "2.5"; media.Files[1].Episode = 0;
        media.Files[2].Extra = new("opening"); media.Files[2].Episode = 99;
        media.Files[3].Extra = new("trailer");
        var remote = new MetadataDocument { Provider = "Bangumi", Id = "fixture-remote", Title = "Remote Work", OriginalTitle = "Original Remote", Date = "2021-01-01", Rating = 8.6, Ids = new() { ["bangumi"] = "fixture-remote" }, Episodes = new() { new("remote-id", 8, 11, "Cross-season episode", "", "2021-02-01", 23, "") { Rating = 8.2, VoteCount = 18 } }, Seasons = new() { new(8, "Remote Season", "Remote overview", "2021-01-01", "") }, CreditsLoaded = true };
        var remoteKey = EpisodeMatching.Key(remote, media.Kind); media.Documents[remoteKey] = remote;
        media.EpisodeBindings[media.Files[4].Path] = new(remoteKey, remote.Episodes[0] with { Rating = null }, 1, 3);
        media.Edits.Episodes[media.Files[5].Path] = new() { Title = "Edited episode", OriginalTitle = "Edited original", Date = "2025-03-02", Rating = 9.1, Ids = new() { ["custom"] = "edited" } };
        media.Files[6].Episode = 500; media.Files[6].LocalMetadata = new() { Title = "Local episode", Date = "2022-01-01", Rating = 5.2 };
        doc.Episodes.Add(doc.Episodes[7] with { Id = "duplicate-id" });
        media.EpisodeBindings[media.Files[8].Path] = new("missing-document", doc.Episodes[8], 1, 5);
        media.Files[9].Extra = new("extra"); media.Files[9].Episode = 0; media.Files[9].Exclusion = "excluded"; media.Files[9].ManuallyExcluded = true;
        media.Files[10].Extra = new("extra"); media.Files[10].Episode = 1; media.Files[10].Exclusion = "excluded"; media.Files[10].ManuallyExcluded = true;
        doc.Episodes[12] = doc.Episodes[12] with { Rating = null };
        doc.Episodes.Add(doc.Episodes[12] with { Season = 8, Number = 200, Rating = 9.9 });
        media.EpisodeBindings[media.Files[12].Path] = new(EpisodeMatching.Key(doc, media.Kind), doc.Episodes[12], 1, 7);
        media.Files[12].LocalMetadata = new() { Title = "Local rating fallback", Rating = 5.4 };
        media.Files[18].Season = 0; media.Files[18].Episode = 1; media.LocalSeasons[0] = new() { Title = "Local special", Date = "2020-01-01", Rating = 4.3 };
        media.Files[19].Season = 3; media.LocalSeasons[3] = new() { Title = "Local Season", Date = "2020-01-01", Rating = 4.7 };
        media.Edits.Seasons[2] = new() { Title = "Edited season", Date = "2025-01-01", Rating = 9.2 };
        var bangumi = Series(3, 1); bangumi.Folder = "C:/synthetic/bangumi"; bangumi.Title = "Bangumi Fixture";
        bangumi.Matches.Clear(); bangumi.Documents.Clear(); bangumi.Matches[1] = new(new("Bangumi", remote.Id, remote.Title, remote.OriginalTitle, remote.Year, remote.Overview, ""), remote); bangumi.Documents[remoteKey] = remote;
        for (var i = 0; i < bangumi.Files.Count; i++) { bangumi.Files[i].Path = $"C:/synthetic/bangumi/{i}.mkv"; bangumi.EpisodeBindings[bangumi.Files[i].Path] = new(remoteKey, remote.Episodes[0], 1, i + 1); }
        var empty = Series(0, 1); empty.Folder = "C:/synthetic/empty"; empty.Title = "Empty Fixture"; empty.ScanWarning = "synthetic warning";
        var movie = new LibraryMedia { Folder = "C:/synthetic/movie", Title = "Movie Fixture", Kind = LibraryMediaKind.Movie, Year = 2001, ExistingIds = new() { ["imdb"] = "fixture-imdb" }, LocalMetadata = new() { Title = "Local movie", OriginalTitle = "Movie Original", Rating = 6.1, Date = "2002-01-01" }, Files = new() { new() { Path = "C:/synthetic/movie/movie.mkv" } } };
        movie.Edits.Work = new() { Title = "Edited movie", Date = "2003-01-01", Rating = 7.1, Ids = new() { ["custom"] = "movie-edit" } };
        return new() { media, bangumi, empty, movie };
    }
    public static HashSet<string> Expanded(IEnumerable<LibraryMedia> media)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in media)
        {
            expanded.Add(m.Folder);
            foreach (var season in m.Files.Select(f => f.Season).Concat(new[] { -3, -2, -1 })) expanded.Add(m.Folder + "|season:" + season);
        }
        return expanded;
    }
}
