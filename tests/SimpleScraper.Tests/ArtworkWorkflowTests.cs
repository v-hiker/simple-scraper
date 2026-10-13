using System.Net;
using System.Text;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class ArtworkWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        LibraryMedia Fixture(string name, bool flat = false)
        {
            var folder = Path.Combine(temporary, name); Directory.CreateDirectory(folder);
            foreach (var season in new[] { 0, 1, 2 })
            {
                var location = flat ? folder : Path.Combine(folder, $"Season {season:00}"); Directory.CreateDirectory(location);
                File.WriteAllText(Path.Combine(location, $"S{season:00}E01.mkv"), "isolated video");
            }
            var media = new LibraryScanner().Scan(folder, LibraryMediaKind.Series, false).Single();
            var doc = new MetadataDocument { Provider = "TMDB", Id = "99", Title = "Art series", PosterUrl = "https://test/show", BackdropUrl = "https://test/backdrop", Cast = new() { new() { Name = "Actor One", ProfilePath = "/actor.jpg" } },
                Seasons = new() { new(0, "Specials", "SP", "2020-01-01", "https://test/specials"), new(1, "First season", "Season one plot", "2020-01-01", "https://test/one"), new(2, "Second season", "Season two plot", "2021-01-01", "https://test/two") },
                Episodes = new() { new("1", 0, 1, "SP", "", "", 20, "https://test/still0"), new("2", 1, 1, "Episode one", "", "", 20, "https://test/still1"), new("3", 2, 1, "Episode two", "", "", 20, "https://test/still2") } };
            foreach (var season in new[] { 0, 1, 2 }) media.Matches[season] = new(new("TMDB", "99", "Art series", "", 2020, "", doc.PosterUrl), doc);
            return media;
        }
        var requests = new List<string>();
        var downloader = new ImageDownloadService(new HttpClient(new WorkflowHandler(r => { requests.Add(r.RequestUri!.AbsolutePath); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(r.RequestUri.AbsolutePath)) }; })));
        var people = new ActorImageCache(Path.Combine(temporary, "artwork-people"));
        var output = new LibraryOutputService(downloader, peopleDirectory: people.DirectoryPath);
        var whole = Fixture("art-whole"); var report = await output.ScrapeAsync(whole, MediaOutputProfile.Jellyfin, false);
        check(report.Images == 9 && people.FindImage("TMDB", 0, "Actor One", "/actor.jpg") != null && !Directory.Exists(Path.Combine(whole.Folder, ".actors")), "full series downloads artwork and shared actor photo without a media-folder actors directory");
        check(File.ReadAllText(Path.Combine(whole.Folder, "Season 01", "poster.jpg")) == "/one" && File.ReadAllText(Path.Combine(whole.Folder, "Season 02", "poster.jpg")) == "/two", "each confirmed season gets its own poster without borrowing another season's image");
        check(XDocument.Load(Path.Combine(whole.Folder, "tvshow.nfo")).Root?.Element("actor")?.Element("thumb")?.Value == "https://image.tmdb.org/t/p/original/actor.jpg", "series NFO retains the portable source URL instead of the private app cache path");
        check(XDocument.Load(Path.Combine(whole.Folder, "Season 02", "season.nfo")).Root?.Element("plot")?.Value == "Season two plot", "season NFO uses season metadata rather than series metadata");
        var repeated = await output.ScrapeAsync(whole, MediaOutputProfile.Jellyfin, false);
        check(repeated.Images == 0 && repeated.Preserved == 7, "repeat scraping preserves existing NFOs and pictures and counts only newly downloaded images");
        await output.ScrapeAsync(whole, MediaOutputProfile.Jellyfin, true, downloadActorPhotos: false);
        check(XDocument.Load(Path.Combine(whole.Folder, "tvshow.nfo")).Root?.Element("actor")?.Element("thumb")?.Value == "https://image.tmdb.org/t/p/original/actor.jpg", "NFO updates keep the source photo URL independently of app-cache downloads");
        var single = Fixture("art-single"); single.Files = single.Files.Where(f => f.Season == 1).ToList();
        await output.ScrapeAsync(single, MediaOutputProfile.Emby, false, episodesOnly: true);
        check(File.Exists(Path.ChangeExtension(single.Files.Single().Path, ".nfo")) && !File.Exists(Path.Combine(single.Folder, "tvshow.nfo")) && !File.Exists(Path.Combine(single.Folder, "Season 01", "poster.jpg")) && !Directory.Exists(Path.Combine(single.Folder, ".actors")), "single episode scraping does not write series, season or actor assets outside its scope");
        var seasonOnly = Fixture("art-one-season"); await output.ScrapeAsync(seasonOnly, MediaOutputProfile.Jellyfin, false, seasonOnly: 2);
        check(File.Exists(Path.Combine(seasonOnly.Folder, "Season 02", "poster.jpg")) && File.Exists(Path.Combine(seasonOnly.Folder, "Season 02", "season.nfo")) && !File.Exists(Path.Combine(seasonOnly.Folder, "tvshow.nfo")) && !File.Exists(Path.Combine(seasonOnly.Folder, "Season 01", "poster.jpg")), "season scraping includes its poster and NFO without touching the show or other seasons");
        check(new LibraryScanner().Scan(seasonOnly.Folder, LibraryMediaKind.Series, false).Single().SeasonNfoPaths.Keys.SequenceEqual(new[] { 2 }), "scanner reports actual season NFO independently of series NFO");
        var flat = Fixture("art-flat", true); await output.ScrapeAsync(flat, MediaOutputProfile.Emby, false);
        var scanned = new LibraryScanner().Scan(flat.Folder, LibraryMediaKind.Series, false).Single();
        check(scanned.SeasonArtworkPaths.Count == 3 && scanned.SeasonArtworkPaths[0]["poster"] == Path.Combine(flat.Folder, "Specials", "poster.jpg") && scanned.PosterPath == Path.Combine(flat.Folder, "poster.jpg") && scanned.SeasonNfoPaths.Count == 3 && !File.Exists(Path.Combine(flat.Folder, "season.nfo")), "flat episodes get separate season folders, NFOs and artwork without mixing series metadata");
        check(flat.Files.All(f => File.Exists(f.Path)), "scraping season information alone never moves the flat videos");
        File.Delete(Path.Combine(flat.Folder, "poster.jpg"));
        check(new LibraryScanner().Scan(flat.Folder, LibraryMediaKind.Series, false).Single().PosterPath == null, "a season poster is never mistaken for the series poster");
        var existing = Fixture("art-existing"); var png = Path.Combine(existing.Folder, "Season 01", "poster.png"); File.WriteAllText(png, "custom poster");
        await output.ScrapeAsync(existing, MediaOutputProfile.Jellyfin, false, seasonOnly: 1);
        check(File.ReadAllText(png) == "custom poster" && !File.Exists(Path.ChangeExtension(png, ".jpg")) && XDocument.Load(Path.Combine(existing.Folder, "Season 01", "season.nfo")).Root?.Element("thumb")?.Value == "poster.png", "existing PNG poster is preserved and referenced without creating a competing JPEG");
        var disabled = Fixture("art-disabled"); await output.ScrapeAsync(disabled, MediaOutputProfile.Jellyfin, false, downloadArtwork: false, downloadActorPhotos: false);
        check(!Directory.EnumerateFiles(disabled.Folder, "*.jpg", SearchOption.AllDirectories).Any() && XDocument.Load(Path.Combine(disabled.Folder, "tvshow.nfo")).Root?.Element("art")?.Element("poster")?.Value == "https://test/show", "disabled artwork downloads leave no local image references to nonexistent files");
        var cached = Fixture("art-old-cache"); var cachedDoc = cached.Matches[1].Document; cachedDoc.Seasons.Clear();
        var client = new TmdbApiClient("test", "zh-CN", new HttpClient(new WorkflowHandler(r => new(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"Loaded season\",\"poster_path\":\"/loaded.jpg\",\"overview\":\"Season plot\",\"episodes\":[]}", Encoding.UTF8, "application/json") })));
        await new LibraryOutputService(downloader, client).ScrapeAsync(cached, MediaOutputProfile.Jellyfin, false, seasonOnly: 2);
        check(cachedDoc.Seasons.Single().Number == 2 && File.Exists(Path.Combine(cached.Folder, "Season 02", "poster.jpg")), "old TMDB matches supplement season artwork without requiring a new episode match");
        var missing = Fixture("art-missing"); var missingDoc = missing.Matches[0].Document; missingDoc.Provider = "Bangumi"; missingDoc.Seasons.Clear();
        var missingReport = await output.ScrapeAsync(missing, MediaOutputProfile.Jellyfin, false, seasonOnly: 0);
        check(!missingReport.Warnings.Any(w => w.EndsWith(SimpleScraper.Localization.Localizer.Text("The selected source has no season poster."))) && !File.Exists(Path.Combine(missing.Folder, "Season 00", "poster.jpg")) && File.Exists(Path.Combine(missing.Folder, "Season 00", "season.nfo")), "missing specials artwork stays silent and is never filled with a regular season poster");
        await output.ScrapeAsync(missing, MediaOutputProfile.Jellyfin, false, seasonOnly: 2);
        check(File.Exists(Path.Combine(missing.Folder, "Season 02", "poster.jpg")), "legacy confirmed Bangumi subject supplies its season poster without rematching");
        var bangumiSp = Fixture("art-bangumi-sp"); var bangumiDoc = bangumiSp.Matches[1].Document; bangumiDoc.Provider = "Bangumi"; bangumiDoc.Seasons.Clear(); bangumiSp.Matches.Remove(0);
        var specialFile = bangumiSp.Files.Single(f => f.Season == 0); bangumiSp.Documents[EpisodeMatching.Key(bangumiDoc, LibraryMediaKind.Series)] = bangumiDoc; bangumiSp.EpisodeBindings[specialFile.Path] = new(EpisodeMatching.Key(bangumiDoc, LibraryMediaKind.Series), bangumiDoc.Episodes.Single(e => e.Season == 0), 0, 1);
        await output.ScrapeAsync(bangumiSp, MediaOutputProfile.Jellyfin, false);
        check(File.Exists(Path.Combine(bangumiSp.Folder, "tvshow.nfo")) && File.Exists(Path.Combine(bangumiSp.Folder, "poster.jpg")), "a reviewed S00 binding does not prevent series artwork export when the first regular Bangumi season is confirmed");
        var failure = Fixture("art-network-failure");
        var failOutput = new LibraryOutputService(new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.NotFound)))));
        var failReport = await failOutput.ScrapeAsync(failure, MediaOutputProfile.Jellyfin, false, seasonOnly: 1);
        check(failReport.Images == 0 && failReport.Warnings.Count > 0 && File.Exists(Path.Combine(failure.Folder, "Season 01", "season.nfo")), "failed image requests do not block successful metadata export or claim image success");
    }
}
