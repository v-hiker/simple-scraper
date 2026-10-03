using SimpleScraper.Models;
using SimpleScraper.Services;

static class ExperienceWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var doc = new MetadataDocument { Provider = "TMDB", Id = "20", Title = "Series", OriginalTitle = "原始剧名", Episodes = new() { new("1", 1, 1, "First", "", "", 0, "") } };
        var media = new LibraryMedia { Folder = Path.Combine(temporary, "Series"), Title = "Series", Kind = LibraryMediaKind.Series, Matches = new() { [1] = new(new("TMDB", "20", "Series", "", 0, "", ""), doc) }, Files = new() { new() { Path = "S01E01.mkv", Season = 1, Episode = 1 }, new() { Path = "S02E01.mkv", Season = 2, Episode = 1 } } };
        check(LibraryReview.MatchStatus(media) == "Partially matched" && LibraryReview.NeedsAttention(media), "a matched season does not hide another unmatched season");
        check(LibraryReview.MatchStatus(media, 1) == "Matched" && LibraryReview.MatchStatus(media, 2) == "Not matched", "season status reflects actual local episode assignments");
        check(LibraryReview.MatchesQuery(media, " S02E01 ") && LibraryReview.MatchesQuery(media, "原始剧名"), "library filter searches filenames and original titles, trimming whitespace");
        media.Files.RemoveAt(1);
        check(!LibraryReview.NeedsAttention(media), "complete episode matching clears attention state");
        var extra = new LocalMediaFile { Path = "NCOP01.mkv", Extra = new("NCOP01"), Exclusion = "extra" }; media.Files.Add(extra);
        check(LibraryReview.IsExtra(media, extra) && LibraryReview.MatchStatus(media) == "Matched" && !LibraryReview.NeedsAttention(media), "extras do not turn a completely matched series into a partially matched series");
        extra.ManuallyExcluded = true; extra.Episode = 0;
        check(LibraryReview.IsExtra(media, extra), "old 0/0 skips still display as extras");
        extra.Episode = 1;
        check(!LibraryReview.IsExtra(media, extra), "explicitly excluded extras remain in excluded files");
        media.Files.Add(new() { Path = "S01E11.5.mkv", Season = 1, NeedsReview = true, Exclusion = "fractional", SourceNumber = "11.5" });
        check(LibraryReview.MatchStatus(media, 1) == "Partially matched" && LibraryReview.NeedsAttention(media), "fractional episodes awaiting review appear in attention filtering");
        var library = new MediaLibrary("One", temporary, LibraryMediaKind.Auto, true);
        check(LibraryReview.DuplicateLibrary(new[] { library }, library with { Name = "Another", Path = temporary + Path.DirectorySeparatorChar, OutputProfile = "Emby" }), "duplicate library identity ignores label, output profile and trailing separator");
        check(!LibraryReview.DuplicateLibrary(new[] { library }, library with { IsRoot = false }), "different scanning modes can coexist");
        check(LibraryReview.DuplicateLibrary(new[] { library }, library with { AutoLayout = true, IsRoot = false }), "auto layout does not duplicate an existing folder when inferred mode changes");
        var layoutRoot = Path.Combine(temporary, "Auto layout"); Directory.CreateDirectory(layoutRoot);
        var showFolder = Path.Combine(layoutRoot, "Example (2020)"); Directory.CreateDirectory(Path.Combine(showFolder, "Season 01")); Directory.CreateDirectory(Path.Combine(showFolder, "Specials")); Directory.CreateDirectory(Path.Combine(showFolder, "OPED"));
        File.WriteAllText(Path.Combine(showFolder, "Season 01", "S01E01.mkv"), "video"); File.WriteAllText(Path.Combine(showFolder, "Specials", "S00E01.mkv"), "video");
        var movieFolder = Path.Combine(layoutRoot, "Film (2021)"); Directory.CreateDirectory(movieFolder); File.WriteAllText(Path.Combine(movieFolder, "film.mkv"), "video");
        check(LibraryScanner.DetectLibraryRoot(layoutRoot), "collection of title folders is a library root");
        check(!LibraryScanner.DetectLibraryRoot(showFolder), "seasons and specials remain one series without a root checkbox");
        check(!LibraryScanner.DetectLibraryRoot(movieFolder), "direct movie file is one movie directory");
        var seasonFolder = Path.Combine(temporary, "第三季"); Directory.CreateDirectory(seasonFolder); File.WriteAllText(Path.Combine(seasonFolder, "[Group] Series [01].mkv"), "video");
        check(!LibraryScanner.DetectLibraryRoot(seasonFolder), "standalone Chinese third-season directory stays a single title");
        var nfoFolder = Path.Combine(temporary, "NFO layout"); Directory.CreateDirectory(nfoFolder); File.WriteAllText(Path.Combine(nfoFolder, "tvshow.nfo"), "<tvshow/>");
        check(!LibraryScanner.DetectLibraryRoot(nfoFolder), "title-level tvshow NFO indicates a title without videos at root");
        var scanner = new LibraryScanner(); var autoLibrary = new MediaLibrary("Auto", layoutRoot, LibraryMediaKind.Auto, false) { AutoLayout = true };
        check(scanner.Scan(autoLibrary).Count == 2, "automatic root layout scans both works despite a stale saved IsRoot value");
        var autoShow = scanner.Scan(autoLibrary with { Path = showFolder, IsRoot = true }).Single();
        check(autoShow.Kind == LibraryMediaKind.Series && autoShow.Files.Count == 2, "automatic title layout scans seasons together rather than treating them as separate works");
        check(scanner.Scan(autoLibrary with { Path = movieFolder, IsRoot = true }).Single().Kind == LibraryMediaKind.Movie, "automatic single movie layout corrects a stale saved root mode");
        var extrasOnly = Path.Combine(temporary, "Extra layout"); Directory.CreateDirectory(Path.Combine(extrasOnly, "OPED")); File.WriteAllText(Path.Combine(extrasOnly, "OPED", "NCOP01.mkv"), "video");
        check(!LibraryScanner.DetectLibraryRoot(extrasOnly) && scanner.Scan(autoLibrary with { Path = extrasOnly }).Single().Folder == extrasOnly, "extras-only directory remains a title instead of separate OPED library entries");
        var legacyLibrary = System.Text.Json.JsonSerializer.Deserialize<MediaLibrary>("{\"Name\":\"Legacy\",\"Path\":\"x\",\"Kind\":2,\"IsRoot\":false}")!;
        check(!legacyLibrary.AutoLayout && !legacyLibrary.IsRoot, "old library settings preserve explicit scanning modes when auto flag is absent");
        using (var cancelledLayout = new CancellationTokenSource())
        {
            cancelledLayout.Cancel(); var cancelled = false;
            try { scanner.Scan(autoLibrary, cancelledLayout.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "automatic folder inference respects scan cancellation");
        }
        var groups = new List<RenamePreviewGroup> { new() { File = new() { Path = "OP01.mkv" }, State = RenamePreviewState.Changed, Selected = true }, new() { File = new() { Path = "ED01.mkv" }, State = RenamePreviewState.Changed, Selected = true }, new() { File = new() { Path = "OP02.mkv" }, State = RenamePreviewState.Conflict } };
        RenameReview.SelectVisible(groups, 1, " OP ", false);
        check(!groups[0].Selected && groups[1].Selected && !groups[2].Selected, "clear visible selection leaves hidden selection unchanged and never selects conflicts");
        RenameReview.SelectVisible(groups, 0, "OP", true);
        check(groups[0].Selected && groups[1].Selected && !groups[2].Selected, "select visible changes respects both query and eligible state");
        check(RenameReview.Visible(groups, 2, "OP").Single() == groups[2], "conflicts filter exposes only relevant conflicts");

        foreach (var name in new[] { "TMDB", "Bangumi" })
        {
            using var cancellation = new CancellationTokenSource(); using var handler = new WaitingHandler(); using var http = new HttpClient(handler);
            IMetadataProvider provider = name == "TMDB" ? new TmdbMetadataProvider(new("test", "en", http, cancellation.Token)) : new BangumiMetadataProvider(http, cancellation.Token);
            var pending = MetadataProviderSearch.SearchAsync(new[] { provider }, "Series", LibraryMediaKind.Series, cancellation.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); var cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(cancelled, name + " cancels both the search wait and actual HTTP request without reporting a connection error");
        }
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        var neverStarted = new WaitingHandler(); using var preHttp = new HttpClient(neverStarted); var rejected = false;
        try { await new TmdbApiClient("test", "en", preHttp, preCancelled.Token).GetTvSeasonDetailsAsync(1, 1); } catch (OperationCanceledException) { rejected = true; }
        check(rejected && !neverStarted.Started.Task.IsCompleted, "cancelled TMDB metadata load never starts another season request");
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new(); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
    }
}
