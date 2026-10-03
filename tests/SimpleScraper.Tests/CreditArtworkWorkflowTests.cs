using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class CreditArtworkWorkflowTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var requests = new List<string>();
        var http = new HttpClient(new WorkflowHandler(r =>
        {
            requests.Add(r.RequestUri!.AbsolutePath);
            var json = r.RequestUri.AbsolutePath.EndsWith("characters") ? """
                [{"name":"Main role","relation":"主角","actors":[{"id":10,"name":"Voice actor","images":{"large":"https://test/person.jpg"}}]},
                 {"name":"Second role","relation":"配角","actors":[{"id":10,"name":"Voice actor","images":{"large":"https://test/person.jpg"}}]},
                 {"name":"Unvoiced character","relation":"配角","actors":[]}]
                """ : r.RequestUri.AbsolutePath.EndsWith("persons") ? """
                [{"id":20,"type":1,"name":"Director One","relation":"导演","images":{}},
                 {"id":20,"type":1,"name":"Director One","relation":"脚本","images":{}},
                 {"id":30,"type":2,"name":"Studio One","relation":"动画制作","images":{}}]
                """ : r.RequestUri.AbsolutePath.Contains("episodes") ? "{\"data\":[{\"id\":1,\"type\":0,\"ep\":1,\"sort\":1,\"name\":\"Episode\"}]}" : "{\"id\":99,\"name\":\"Series\",\"date\":\"2026-01-01\",\"images\":{}}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
        var bangumi = new BangumiMetadataProvider(http);
        var document = await bangumi.LoadAsync("99", LibraryMediaKind.Series, new[] { 3 });
        check(document.CreditsLoaded && document.Cast.Count == 1 && document.Cast[0].Name == "Voice actor" && document.Cast[0].Character == "Main role / Second role", "Bangumi maps real voice actors to roles, merges repeated performers and never exports an unvoiced character as an actor");
        check(document.Cast[0].ProfilePath == "https://test/person.jpg" && document.Crew.Count == 2 && document.Studios.Single() == "Studio One", "Bangumi portraits belong to the performer and production companies stay separate from individual crew");
        var count = requests.Count; await MetadataCredits.EnsureAsync(document, LibraryMediaKind.Series, null, bangumi);
        check(requests.Count == count, "loaded credits reuse saved data without repeating HTTP requests");
        var legacy = new MetadataDocument { Provider = "Bangumi", Id = "99", Title = "Saved custom title", Episodes = document.Episodes.ToList() };
        await MetadataCredits.EnsureAsync(legacy, LibraryMediaKind.Series, null, bangumi);
        check(legacy.CreditsLoaded && legacy.Cast.Count == 1 && legacy.Title == "Saved custom title" && legacy.Episodes.Single().Season == 3, "old Bangumi matches supplement people without changing title, episodes or output numbering");
        var concurrent = new MetadataDocument { Provider = "Bangumi", Id = "99" }; count = requests.Count;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => MetadataCredits.EnsureAsync(concurrent, LibraryMediaKind.Series, null, bangumi)));
        check(requests.Count - count == 2 && concurrent.CreditsLoaded, "concurrent requests for the same cached document share one cast and crew lookup");
        var partial = new MetadataDocument { Provider = "Bangumi", Id = "99", Cast = document.Cast.ToList() };
        await new BangumiMetadataProvider(new HttpClient(new WorkflowHandler(r => r.RequestUri!.AbsolutePath.EndsWith("characters") ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent("[]") }))).LoadCreditsAsync(partial);
        check(!partial.CreditsLoaded && partial.Cast.Count == 1 && partial.Warnings.Count == 1, "failed cast request retains existing actors, reports failure and permits retry");
        await bangumi.LoadCreditsAsync(partial);
        check(partial.CreditsLoaded && partial.Warnings.Count == 0, "successful credit retry clears the old failure message");
        var logoRequests = new List<string>();
        var logos = new TmdbApiClient("test", "zh-CN", new HttpClient(new WorkflowHandler(r =>
        {
            logoRequests.Add(r.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"logos\":[{\"file_path\":\"/english.png\",\"iso_639_1\":\"en\",\"vote_average\":9},{\"file_path\":\"/neutral.png\",\"iso_639_1\":null},{\"file_path\":\"/chinese.svg\",\"iso_639_1\":\"zh\",\"vote_average\":7}]}") };
        })));
        check((await logos.GetClearLogoAsync(99, true)).EndsWith("/chinese.png") && logoRequests.Single().StartsWith("/3/tv/99/images?") && !logoRequests.Single().Contains("language="), "TV logo query includes all languages, prefers configured language and requests the PNG rendition of SVG");
        check((await logos.GetClearLogoAsync(99, false)).EndsWith("/chinese.png") && logoRequests.Last().StartsWith("/3/movie/99/images?"), "movie logo query uses movie identity rather than the TV endpoint");
        var neutral = new TmdbApiClient("test", "ja", new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"logos\":[{\"file_path\":\"/english.png\",\"iso_639_1\":\"en\"},{\"file_path\":\"/neutral.png\",\"iso_639_1\":null}]}") })));
        check((await neutral.GetClearLogoAsync(99, true)).EndsWith("/neutral.png"), "missing localized clearlogo falls back to an actual neutral logo");
        var noLogo = new TmdbApiClient("test", "en", new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"logos\":[]}") })));
        check(await noLogo.GetClearLogoAsync(99, true) == "", "empty source logo catalog never borrows a backdrop or poster");
        var download = new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) })));
        foreach (var profile in new[] { MediaOutputProfile.Jellyfin, MediaOutputProfile.Emby })
        {
            var folder = Path.Combine(temporary, "credits-export-" + profile.Name); Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "S03E01.mkv"); File.WriteAllText(file, "isolated video");
            document.ClearLogoUrl = "https://test/logo.png";
            var media = new LibraryMedia { Folder = folder, Title = "Series", Kind = LibraryMediaKind.Series, Files = new() { new() { Path = file, Season = 3, Episode = 1 } }, Matches = new() { [3] = new(new("Bangumi", "99", "Series", "", 2026, "", ""), document) } };
            var people = new ActorImageCache(Path.Combine(temporary, "credit-people-" + profile.Name));
            var output = new LibraryOutputService(download, peopleDirectory: people.DirectoryPath);
            var report = await output.ScrapeAsync(media, profile, false);
            var show = XDocument.Load(Path.Combine(folder, "tvshow.nfo")).Root!;
            check(report.Images == 2 && File.ReadAllBytes(Path.Combine(folder, "clearlogo.png")).SequenceEqual(Png) && show.Element("art")?.Element("clearlogo")?.Value == "clearlogo.png", profile.Name + " exports source PNG clearlogo, preserves its bytes and references a real local image");
            check(show.Element("actor")?.Element("role")?.Value == "Main role / Second role" && show.Element("director")?.Value == "Director One" && show.Element("credits")?.Value == "Director One" && show.Element("studio")?.Value == "Studio One", "NFO exports cast, roles, director, writing credits and studio");
            check(XDocument.Load(Path.ChangeExtension(file, ".nfo")).Root?.Element("actor")?.Element("thumb")?.Value == "https://test/person.jpg" && people.FindImage("Bangumi", 10, "Voice actor", "https://test/person.jpg") != null && !Directory.Exists(Path.Combine(folder, ".actors")), "episode NFO keeps the performer URL while portrait data is cached outside the media directory");
            var scan = new LibraryScanner().Scan(folder, LibraryMediaKind.Series, false).Single();
            check(scan.ArtworkPaths["clearlogo"].EndsWith("clearlogo.png") && scan.LocalMetadata!.Crew.Count == 2 && scan.LocalMetadata.Studios.Count == 1 && scan.LocalMetadata.Cast[0].ProfilePath == "https://test/person.jpg" && scan.LocalMetadata.Provider == "Bangumi" && scan.LocalMetadata.Cast[0].Id == 10, "scanner restores logo, performer identity, photo URL, director, writer and studio from exported NFO");
            check(document.Episodes.Single().OriginalTitle == "Episode", "Bangumi keeps original episode name separately from display title");
            var repeat = await output.ScrapeAsync(media, profile, false);
            check(repeat.Images == 0, "repeat scrape preserves local clearlogo and actor images");
            File.Delete(Path.Combine(folder, "clearlogo.png"));
            await output.ScrapeAsync(media, profile, false, episodesOnly: true);
            check(!File.Exists(Path.Combine(folder, "clearlogo.png")), "single episode scrape never creates series-level logos");
            await output.ScrapeAsync(media, profile, false, downloadArtwork: false, downloadActorPhotos: false);
            check(!File.Exists(Path.Combine(folder, "clearlogo.png")), "disabled artwork setting also disables clearlogo download");
            await output.ScrapeAsync(media, profile, false);
            File.Move(Path.Combine(folder, "clearlogo.png"), Path.Combine(folder, "logo.png"));
            var alias = await output.ScrapeAsync(media, profile, true);
            check(alias.Images == 0 && !File.Exists(Path.Combine(folder, "clearlogo.png")) && XDocument.Load(Path.Combine(folder, "tvshow.nfo")).Root?.Element("art")?.Element("clearlogo")?.Value == "logo.png", "legacy logo.png is recognized and preserved without a duplicate download");
        }
        var badPath = Path.Combine(temporary, "invalid-clearlogo.png");
        check(!await new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>not a PNG</html>") }))).DownloadPngAsync("https://test/bad", badPath) && !File.Exists(badPath) && !Directory.GetFiles(temporary, "invalid-clearlogo.png.*.download").Any(), "HTML or SVG responses never become a fake PNG and temporary downloads are cleaned up");
        var tmdb = new TmdbApiClient("test", "en", new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":99,\"name\":\"Show\",\"production_companies\":[{\"id\":4,\"name\":\"Studio\"}],\"credits\":{\"cast\":[{\"id\":10,\"name\":\"Actor\",\"character\":\"Role\"}],\"crew\":[{\"id\":20,\"name\":\"Director\",\"job\":\"Director\",\"department\":\"Directing\"}]}}") })));
        var oldTmdb = new MetadataDocument { Provider = "TMDB", Id = "99", Title = "Original title" };
        await MetadataCredits.EnsureAsync(oldTmdb, LibraryMediaKind.Series, tmdb, null);
        check(oldTmdb.CreditsLoaded && oldTmdb.Cast.Single().Character == "Role" && oldTmdb.Crew.Single().Job == "Director" && oldTmdb.Studios.Single() == "Studio" && oldTmdb.Title == "Original title", "TMDB cached matches also supplement cast, full TV crew and studio without changing the selected title");
    }

    public static async Task Live(string repository)
    {
        var doc = await new BangumiMetadataProvider().LoadAsync("569116", LibraryMediaKind.Series, new[] { 3 });
        if (!doc.CreditsLoaded || !doc.Cast.Any(a => a.Name == "内田雄馬" && a.Character.Contains("北原伊織")) || !doc.Crew.Any(p => p.Job == "导演") || doc.Studios.Count == 0) throw new Exception("Live Bangumi credit check failed");
        var folder = Path.Combine(repository, "artifacts", "verification", "credits-live-1.10.1"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "bangumi-credits.json"), JsonSerializer.Serialize(doc), new UTF8Encoding(false));
        Console.WriteLine($"LIVE Bangumi: {doc.Cast.Count} performers, {doc.Crew.Count} crew entries, {doc.Studios.Count} studios; expected lead voice actor verified.");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "artifacts", "app", "appsettings.json")));
        var key = config.RootElement.GetProperty("TmdbApiKey").GetString();
        if (string.IsNullOrEmpty(key)) throw new Exception("No configured TMDB key for live logo verification");
        var logo = await new TmdbApiClient(key, "zh-CN").GetClearLogoAsync(438631, false);
        if (string.IsNullOrEmpty(logo) || !await new ImageDownloadService().DownloadPngAsync(logo, Path.Combine(folder, "clearlogo.png"))) throw new Exception("Live TMDB PNG logo verification failed");
        Console.WriteLine("LIVE TMDB: real movie logo API and PNG download verified in isolated fixture.");
    }
}
