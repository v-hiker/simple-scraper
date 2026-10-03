using System.Net;
using System.Text;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class LibraryWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var root = Path.Combine(temporary, "Library");
        var folder = Path.Combine(root, "三月的狮子 (2016)");
        var s1 = Path.Combine(folder, "Season 1"); var s2 = Path.Combine(folder, "Season 2"); var extras = Path.Combine(folder, "OPED");
        Directory.CreateDirectory(s1); Directory.CreateDirectory(s2); Directory.CreateDirectory(extras);
        foreach (var p in new[] { Path.Combine(s1, "S01E01.mkv"), Path.Combine(s1, "S01E11.mkv"), Path.Combine(s1, "S01E11.5.mkv"), Path.Combine(s2, "S02E01.mkv"), Path.Combine(extras, "NCOP01.mkv") }) File.WriteAllText(p, "isolated-video");
        var movieFolder = Path.Combine(root, "Movie (2020)"); Directory.CreateDirectory(movieFolder); File.WriteAllText(Path.Combine(movieFolder, "original.mkv"), "movie");
        var scanner = new LibraryScanner();
        var unnumberedFolder = Path.Combine(temporary, "Unknown numbering"); Directory.CreateDirectory(unnumberedFolder); File.WriteAllText(Path.Combine(unnumberedFolder, "未知文件.mkv"), "isolated-video");
        var unnumbered = scanner.Scan(unnumberedFolder, LibraryMediaKind.Series, false).Single().Files.Single();
        check(unnumbered.NeedsReview && unnumbered.Season == 0 && unnumbered.Episode == 0, "unrecognized numbering stays pending for manual source matching without inventing a local season");
        var mixed = scanner.Scan(root, LibraryMediaKind.Auto, true);
        check(mixed.Count == 2 && mixed.Single(m => m.Kind == LibraryMediaKind.Series).Title == "三月的狮子", "mixed library identifies whole series and movies");
        var media = mixed.Single(m => m.Kind == LibraryMediaKind.Series);
        check(media.Files.Count(f => f.Exclusion.Length == 0) == 3, "scanner excludes fractional episode and NCOP");
        check(media.Files.Single(f => f.Name == "S02E01.mkv").Season == 2, "bare S02E01 gets season and show context");
        var doc = new MetadataDocument { Provider = "TMDB", Id = "67296", Title = "三月的狮子", Date = "2016-10-08", Rating = 8.4, PosterUrl = "https://test/poster", BackdropUrl = "https://test/background", Ids = new() { ["tmdb"] = "67296", ["imdb"] = "tt5176548" }, Episodes = new() { new("1", 1, 1, "前へ", "Plot", "2016-10-08", 25, "https://test/still"), new("11", 1, 11, "Episode 11", "", "", 25, ""), new("23", 2, 1, "第二季第一话", "", "", 25, "") } };
        var candidate = new MetadataCandidate("TMDB", doc.Id, doc.Title, "", 2016, "", "");
        media.Matches[1] = new(candidate, doc); media.Matches[2] = new(candidate, doc);
        var nfo = Path.Combine(s1, "S01E01.nfo");
        File.WriteAllText(nfo, "<episodedetails><title>Original custom title</title><fileinfo><streamdetails><video><codec>hevc</codec></video></streamdetails></fileinfo></episodedetails>");
        var before = File.ReadAllBytes(nfo);
        var output = new LibraryOutputService(new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 255, 216, 255, 217 }) }))));
        var report = await output.ScrapeAsync(media, MediaOutputProfile.Jellyfin, false);
        check(File.ReadAllBytes(nfo).SequenceEqual(before) && report.Preserved == 1, "default scraping preserves existing NFO byte for byte");
        check(File.Exists(Path.Combine(s1, "season.nfo")) && !File.Exists(Path.Combine(s1, "S01E11.5.nfo")), "profile writes season.nfo and skips fractional episode");
        var showNfo = XDocument.Load(Path.Combine(folder, "tvshow.nfo"));
        check(showNfo.Root?.Element("imdb_id")?.Value == "tt5176548" && showNfo.Root?.Element("art")?.Element("fanart")?.Value == "fanart.jpg", "Jellyfin exports TV IMDb and local art conventions");
        await output.ScrapeAsync(media, MediaOutputProfile.Emby, true);
        check(Directory.GetFiles(s1, "S01E01.nfo.bak.*").Length == 0 && XDocument.Load(nfo).Root?.Element("fileinfo") != null, "NFO updates preserve stream details without creating media-folder backups");
        check(XDocument.Load(Path.Combine(folder, "tvshow.nfo")).Root?.Element("fanart")?.Element("thumb")?.Value == "backdrop.jpg" && File.Exists(Path.Combine(folder, "backdrop.jpg")), "Emby profile changes NFO artwork and backdrop filename");
        File.WriteAllText(Path.Combine(s1, "S01E01.zh-CN.ass"), "subtitle"); File.WriteAllText(Path.Combine(s1, "S01E11.5.ass"), "special subtitle");
        var rename = new LibraryRenameService();
        var plan = rename.Preview(media, LibraryRenameService.EpisodeTemplate);
        check(plan.Any(p => p.Source.EndsWith("S01E01.zh-CN.ass")) && plan.All(p => !p.Source.Contains("11.5")) && plan.Any(p => p.Source.Contains("NCOP") && Path.GetFileName(Path.GetDirectoryName(p.Destination)) == "extras"), "rename carries subtitles, protects fractional episodes and organizes NCOP as extras");
        var groups = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate);
        check(groups.Count == 5 && groups.Count(g => g.State == RenamePreviewState.Changed && g.Selected) == 4 && groups.Count(g => g.State == RenamePreviewState.Excluded) == 1 && groups.Single(g => g.IsExtra).File.Name == "NCOP01.mkv", "comparison preview includes extras alongside changed and excluded episode files");
        var first = groups.Single(g => g.File.Name == "S01E01.mkv");
        check(first.Operations.Count == 4 && first.Operations.Any(p => p.Source.EndsWith(".zh-CN.ass")), "one video group includes its NFO, thumbnail and language subtitle atomically");
        first.Selected = false;
        check(groups.Where(g => g.Selected).SelectMany(g => g.Operations).All(p => !first.Operations.Contains(p)), "deselecting one video excludes all its companion operations");
        var occupied = plan.First(p => p.Source.EndsWith("S01E01.mkv")).Destination; File.WriteAllText(occupied, "occupied target");
        var conflictPreview = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate);
        check(conflictPreview.Single(g => g.File.Name == "S01E01.mkv").State == RenamePreviewState.Conflict && conflictPreview.Count(g => g.Selected) == 3, "existing target marks only its video group as unselected conflict, keeping other changes usable");
        File.Delete(occupied); media.Matches.Remove(2);
        check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate).Single(g => g.File.Name == "S02E01.mkv").State == RenamePreviewState.Unmatched, "unmatched season is shown explicitly in comparison preview");
        media.Matches[2] = new(candidate, doc);
        var protectedBytes = File.ReadAllBytes(Path.Combine(s1, "S01E11.5.mkv"));
        rename.Execute(plan);
        check(File.Exists(Path.Combine(s1, "三月的狮子 - S01E01 - 前へ.mkv")) && File.Exists(Path.Combine(s1, "三月的狮子 - S01E01 - 前へ.zh-CN.ass")), "confirmed names and subtitle suffixes are written correctly");
        check(XDocument.Load(Path.Combine(s1, "三月的狮子 - S01E01 - 前へ.nfo")).Root?.Element("thumb")?.Value == "三月的狮子 - S01E01 - 前へ-thumb.jpg", "rename updates local NFO thumbnail references");
        check(protectedBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(s1, "S01E11.5.mkv"))), "excluded video bytes remain unchanged");
        var rescanned = scanner.Scan(folder, LibraryMediaKind.Series, false).Single(); rescanned.Matches = media.Matches;
        check(rescanned.LocalMetadata?.Title == doc.Title && rescanned.NfoPath != null && rescanned.PosterPath != null, "scanner caches existing metadata and artwork without opening video streams");
        check(rename.BuildPreview(rescanned, LibraryRenameService.EpisodeTemplate).Count(g => g.State == RenamePreviewState.Unchanged) == 4, "already renamed episodes and extras remain visible as unchanged without selectable operations");
        var prefs = LibraryColumns.Normalize(new[] { new LibraryColumnPreference { Id = "path", Visible = true, Width = 900 }, new LibraryColumnPreference { Id = "title", Visible = false, Width = 1 }, new LibraryColumnPreference { Id = "path", Width = 100 }, new LibraryColumnPreference { Id = "codec", Visible = true, Width = 100 } });
        check(prefs[0].Id == "path" && prefs[0].Width == 600 && prefs.Single(p => p.Id == "title").Visible && prefs.Single(p => p.Id == "title").Width == 50 && prefs.Count == LibraryColumns.All.Count, "column preferences preserve order, force title visibility and reject duplicate or unknown columns");
        var savedColumns = System.Text.Json.JsonSerializer.Deserialize<LibraryStateStore>(System.Text.Json.JsonSerializer.Serialize(new LibraryStateStore { Columns = prefs, SortColumn = "year", SortDescending = true }))!;
        check(savedColumns.Columns[0].Id == "path" && savedColumns.SortColumn == "year" && savedColumns.SortDescending, "column widths, order and sort settings survive persistence");
        var collisionSource = Path.Combine(s1, "collision.mkv"); var collisionTarget = Path.Combine(s1, "taken.mkv"); File.WriteAllText(collisionSource, "source"); File.WriteAllText(collisionTarget, "target");
        var failed = false; try { rename.Execute(new[] { new FileRename(collisionSource, collisionTarget) }); } catch (IOException) { failed = true; }
        check(failed && File.ReadAllText(collisionSource) == "source" && File.ReadAllText(collisionTarget) == "target", "preflight collision leaves both files untouched");
        var rollbackVideo = Path.Combine(s1, "rollback.mkv"); var rollbackNfo = Path.Combine(s1, "rollback.nfo"); File.WriteAllText(rollbackVideo, "rollback bytes"); File.WriteAllText(rollbackNfo, "<broken>");
        failed = false;
        try { rename.Execute(new[] { new FileRename(rollbackVideo, Path.Combine(s1, "renamed.mkv")), new FileRename(rollbackNfo, Path.Combine(s1, "renamed.nfo")) }); } catch (AggregateException) { failed = true; }
        check(failed && File.ReadAllText(rollbackVideo) == "rollback bytes" && File.ReadAllText(rollbackNfo) == "<broken>" && !File.Exists(Path.Combine(s1, "renamed.mkv")), "a failure after file moves rolls video and NFO back to original names and bytes");
        var calls = new List<string>();
        var bangumi = new BangumiMetadataProvider(new HttpClient(new WorkflowHandler(request =>
        {
            calls.Add(request.RequestUri!.ToString());
            var json = request.Method == HttpMethod.Post ? "{\"data\":[{\"id\":211567,\"name\":\"3月のライオン 第2シリーズ\",\"name_cn\":\"3月的狮子 第二季\",\"date\":\"2017-10-14\",\"summary\":\"plot\",\"images\":{}}]}" : request.RequestUri!.AbsolutePath.Contains("episodes") ? "{\"data\":[{\"id\":123,\"type\":0,\"ep\":1,\"sort\":23,\"name\":\"Episode\",\"name_cn\":\"第二季第一话\",\"duration_seconds\":1500},{\"id\":124,\"type\":0,\"ep\":11.5,\"sort\":33.5,\"name\":\"Fractional\"},{\"id\":125,\"type\":2,\"ep\":2,\"sort\":1,\"name\":\"OP\"}]}" : "{\"id\":211567,\"name\":\"3月のライオン 第2シリーズ\",\"name_cn\":\"3月的狮子 第二季\",\"date\":\"2017-10-14\",\"summary\":\"plot\",\"images\":{},\"rating\":{\"score\":9.1}}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        })));
        var found = await bangumi.SearchAsync("三月", LibraryMediaKind.Series);
        var season2 = await bangumi.LoadAsync(found[0].Id, LibraryMediaKind.Series, new[] { 2 });
        check(season2.Episodes.Count == 2 && season2.Episodes[0].Season == 2 && season2.Episodes[0].Number == 1 && season2.Episodes[1].Season == 0 && season2.Episodes[1].SourceNumber == "11.5", "Bangumi sequel local ep=1 maps S02E01; decimal numbering remains a manual SP candidate with original number; OP ignored");
        failed = false; try { await bangumi.LoadAsync("211567", LibraryMediaKind.Series, new[] { 1, 2 }); } catch (InvalidOperationException) { failed = true; }
        check(failed && calls.Any(p => p.Contains("type=0")), "Bangumi requires explicit one-subject-per-season mapping");
        var sourceSubject = await bangumi.LoadAsync(found[0].Id, LibraryMediaKind.Series, Array.Empty<int>());
        check(sourceSubject.Episodes.Count == 2 && sourceSubject.Episodes.Any(e => e.Season == 0) && sourceSubject.Episodes.Single(e => e.Season > 0).Number == 1, "Bangumi loads the selected subject and specials without any local season selection");
        check(EpisodeMatching.SuggestForResult(media, sourceSubject, new HashSet<int> { 1, 2 }, null, false).Count == 0, "one Bangumi subject cannot automatically overwrite multiple local filename groups");
        var secondGroup = new LibraryMedia { Kind = LibraryMediaKind.Series, Files = new() { new() { Path = "local-second.mkv", Season = 2, Episode = 1 } } };
        var groupSuggestion = EpisodeMatching.SuggestForResult(secondGroup, sourceSubject, new HashSet<int> { 2 }, 1, false);
        check(groupSuggestion["local-second.mkv"].Season == 2 && groupSuggestion["local-second.mkv"].Episode.Id == "123", "a selected filename group can map a loaded Bangumi subject after search without changing provider identity");
        var requestedSeasons = new System.Collections.Concurrent.ConcurrentBag<int>();
        var sourceTmdb = new TmdbMetadataProvider(new TmdbApiClient("test-key", "en-US", new HttpClient(new WorkflowHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/season/")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":99,\"name\":\"Catalog\",\"overview\":\"Plot\",\"seasons\":[{\"season_number\":0},{\"season_number\":1},{\"season_number\":3}]}" , Encoding.UTF8, "application/json") };
            var season = int.Parse(path.Split('/').Last()); requestedSeasons.Add(season);
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"name\":\"Season {season}\",\"poster_path\":\"/season{season}.jpg\",\"overview\":\"Season plot\",\"episodes\":[{{\"id\":{100 + season},\"episode_number\":1,\"name\":\"Episode {season}\"}}]}}", Encoding.UTF8, "application/json") };
        }))));
        var fullSource = await sourceTmdb.LoadAsync("99", LibraryMediaKind.Series, Array.Empty<int>());
        check(requestedSeasons.Order().SequenceEqual(new[] { 0, 1, 3 }) && fullSource.Episodes.Select(e => e.Season).Order().SequenceEqual(new[] { 0, 1, 3 }), "TMDB discovers seasons and specials from its own catalog, including a season absent from local files");
        check(fullSource.Seasons.Count == 3 && fullSource.Seasons.All(s => s.PosterUrl.EndsWith($"/season{s.Number}.jpg") && s.Title == $"Season {s.Number}"), "TMDB provider retains distinct season poster URLs and titles alongside the episode catalog");
        var aggregated = await MetadataProviderSearch.SearchAsync(new IMetadataProvider[] { new FixedProvider("TMDB", false), new FixedProvider("Bangumi", false) }, "test", LibraryMediaKind.Series);
        check(aggregated.Candidates.Count == 2 && aggregated.Candidates.Select(c => c.Provider).Distinct().Count() == 2, "multi-source search aggregates and labels each provider's candidates");
        var partial = await MetadataProviderSearch.SearchAsync(new IMetadataProvider[] { new FixedProvider("TMDB", true), new FixedProvider("Bangumi", false) }, "test", LibraryMediaKind.Series);
        check(partial.Candidates.Count == 1 && partial.Errors.Single().StartsWith("TMDB:"), "a failing source does not hide another source's results");
        failed = false; try { await MetadataProviderSearch.SearchAsync(Array.Empty<IMetadataProvider>(), "test", LibraryMediaKind.Movie); } catch (InvalidOperationException) { failed = true; }
        check(failed, "empty source selection yields an actionable message");
        var sourceConfig = new ConfigService(); sourceConfig.SetDefaultMetadataSources(new[] { "Bangumi" });
        check(new ConfigService().GetDefaultMetadataSources().SequenceEqual(new[] { "Bangumi" }), "default source multi-selection is shared and persisted in settings");
        sourceConfig.SetDefaultMetadataSources(new[] { "TMDB", "Bangumi" });
        var movie = mixed.Single(m => m.Kind == LibraryMediaKind.Movie);
        var md = new MetadataDocument { Provider = "TMDB", Id = "9", Title = "Movie", Date = "2020-01-01", Ids = new() { ["tmdb"] = "9" } }; movie.Matches[-1] = new(new("TMDB", "9", "Movie", "", 2020, "", ""), md);
        await output.ScrapeAsync(movie, MediaOutputProfile.Emby, false);
        check(File.Exists(Path.Combine(movie.Folder, "original.nfo")) && !File.Exists(Path.Combine(movie.Folder, "movie.nfo")), "Emby movie preset exports video basename NFO");
        File.Delete(Path.Combine(movie.Folder, "original.nfo")); await output.ScrapeAsync(movie, MediaOutputProfile.Jellyfin, false);
        check(File.Exists(Path.Combine(movie.Folder, "movie.nfo")), "Jellyfin movie preset exports movie.nfo");
        File.WriteAllText(Path.Combine(movie.Folder, "movie.nfo"), "<movie><originaltitle>Broken</title></movie>");
        var broken = scanner.Scan(movie.Folder, LibraryMediaKind.Movie, false).Single();
        check(broken.ScanWarning.Length > 0 && broken.Files.Count == 1, "malformed existing NFO does not prevent library loading");
        await output.ScrapeAsync(movie, MediaOutputProfile.Jellyfin, true);
        check(XDocument.Load(Path.Combine(movie.Folder, "movie.nfo")).Root?.Name == "movie" && Directory.GetFiles(movie.Folder, "movie.nfo.bak.*").Length == 0, "scrape regenerates malformed NFO without backup copies");
        media.Files.Single(f => f.Name == "S01E11.5.mkv").Exclusion = ""; media.Files.Single(f => f.Name == "S01E11.5.mkv").Season = 1; media.Files.Single(f => f.Name == "S01E11.5.mkv").Episode = 11;
        failed = false; try { rename.Preview(media, LibraryRenameService.EpisodeTemplate); } catch (InvalidOperationException) { failed = true; }
        check(failed, "manual special mapping cannot duplicate an existing regular episode");
        await EpisodeWorkflowTests.Run(temporary, check);
        await ArtworkWorkflowTests.Run(temporary, check);
        await SeasonWorkflowTests.Run(temporary, check);
        await CreditArtworkWorkflowTests.Run(temporary, check);
        await SharedPeopleWorkflowTests.Run(temporary, check);
        await OverviewWorkflowTests.Run(temporary, check);
        await TranslationResponseWorkflowTests.Run(temporary, check);
        SpecialsWorkflowTests.Run(temporary, check);
        LibrarySnapshotWorkflowTests.Run(temporary, check);
        PresentationWorkflowTests.Run(check);
    }

}
sealed class FixedProvider(string name, bool fail) : IMetadataProvider
{
    public string Name => name;
    public Task<List<MetadataCandidate>> SearchAsync(string query, LibraryMediaKind kind) => fail ? throw new HttpRequestException("unavailable") : Task.FromResult(new List<MetadataCandidate> { new(name, "1", "Test", "", 2020, "", "") });
    public Task<MetadataDocument> LoadAsync(string id, LibraryMediaKind kind, IReadOnlyList<int> seasons) => throw new NotImplementedException();
}
sealed class WorkflowHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
}
