using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class SharedPeopleWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var people = new ActorImageCache(Path.Combine(temporary, "shared-people"));
        var requests = 0;
        var images = new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => { Interlocked.Increment(ref requests); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 255, 216, 255, 217 }) }; })));
        var output = new LibraryOutputService(images, peopleDirectory: people.DirectoryPath);
        LibraryMedia Movie(string folderName, string role)
        {
            var folder = Path.Combine(temporary, folderName); Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "original.mkv"); File.WriteAllText(file, "isolated video");
            File.WriteAllText(Path.Combine(folder, "movie.nfo"), "<movie><title>Old</title><originaltitle>Old original</originaltitle><uniqueid type='tmdb'>999999</uniqueid><custom>Keep</custom></movie>");
            var doc = new MetadataDocument { Provider = "TMDB", Id = "77", Title = "New title", OriginalTitle = "Original source title", CreditsLoaded = true, Cast = new() { new() { Id = 7, Name = "Same Actor", Character = role, ProfilePath = "/shared.jpg" } } };
            return new() { Folder = folder, Kind = LibraryMediaKind.Movie, Files = new() { new() { Path = file } }, Matches = new() { [-1] = new(new("TMDB", "77", doc.Title, doc.OriginalTitle, 0, "", ""), doc) } };
        }
        var first = Movie("shared-film-one", "First role"); var firstReport = await output.ScrapeAsync(first, MediaOutputProfile.Jellyfin);
        var nfo = XDocument.Load(Path.Combine(first.Folder, "movie.nfo")).Root!;
        check(firstReport.Written == 1 && firstReport.Preserved == 0 && nfo.Element("title")?.Value == "New title" && nfo.Element("custom")?.Value == "Keep", "default scrape updates existing NFO while retaining custom fields");
        check(nfo.Element("originaltitle")?.Value == "Original source title" && nfo.Element("uniqueid")?.Value == "77" && nfo.Element("scraper")?.Element("url")?.Value == "https://www.themoviedb.org/movie/77", "confirmed source identity and original title replace stale IDs without requiring an ID dictionary");
        check(nfo.Element("actor")?.Element("tmdbid")?.Value == "7" && nfo.Element("actor")?.Element("type")?.Value == "Actor" && nfo.Element("actor")?.Element("thumb")?.Value == "https://image.tmdb.org/t/p/original/shared.jpg", "actor NFO exports actual person ID and remote URL instead of app-specific local paths");
        var second = Movie("shared-film-two", "Second role"); var secondReport = await output.ScrapeAsync(second, MediaOutputProfile.Emby);
        check(requests == 1 && firstReport.Images == 1 && secondReport.Images == 0 && Directory.EnumerateFiles(people.DirectoryPath, "portrait.jpg", SearchOption.AllDirectories).Count() == 1, "the same source actor across libraries shares one photo and avoids repeated HTTP downloads");
        check(XDocument.Load(Path.Combine(first.Folder, "movie.nfo")).Root?.Element("actor")?.Element("role")?.Value == "First role" && XDocument.Load(Path.Combine(second.Folder, "movie.nfo")).Root?.Element("actor")?.Element("role")?.Value == "Second role", "shared person cache never mixes roles belonging to different movies");
        var person = JsonSerializer.Deserialize<CachedPerson>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(people.ImagePath("TMDB", 7, "Same Actor", "/shared.jpg"))!, "person.json")))!;
        check(person.Id == 7 && person.Provider == "TMDB" && person.Name == "Same Actor" && person.ImageUrl.StartsWith("https://"), "shared actor metadata is stored with the cached portrait in the app cache");
        check(!Directory.Exists(Path.Combine(first.Folder, ".actors")) && !Directory.Exists(Path.Combine(second.Folder, ".actors")) && !Directory.EnumerateFiles(first.Folder, "*.bak*", SearchOption.AllDirectories).Any() && !Directory.EnumerateFiles(second.Folder, "*.bak*", SearchOption.AllDirectories).Any(), "scraping creates neither actors directories nor NFO backup copies beside media");
        check(people.ImagePath("TMDB", 7, "Actor", null) != people.ImagePath("Bangumi", 7, "Actor", null) && people.ImagePath("TMDB", 7, "Actor", null) != people.ImagePath("TMDB", 8, "Actor", null), "different providers and same-name people never collide by person ID");
        check(people.ImagePath("TMDB", 0, "One", "") != people.ImagePath("TMDB", 0, "Two", "") && people.ImagePath("../escape", 3, "Actor", "").StartsWith(people.DirectoryPath + Path.DirectorySeparatorChar), "missing IDs use stable fallback identities and unsafe provider names cannot escape the cache");
        var beforeRequests = requests;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => people.EnsureAsync("TMDB", 9, "Parallel", "/parallel.jpg", images)));
        check(requests == beforeRequests + 1, "concurrent shared-person downloads are serialized and fetched once");

        var histories = Path.Combine(temporary, "rename-history"); var rename = new LibraryRenameService(histories);
        var original = first.Files.Single().Path; var destination = Path.Combine(first.Folder, "New title.mkv");
        rename.Execute(new[] { new FileRename(original, destination) });
        var journal = JsonSerializer.Deserialize<RenameHistoryRecord>(File.ReadAllText(Directory.GetFiles(histories, "*.json").Single()))!;
        check(journal.Status == "Completed" && journal.Files.Single().OriginalPath == original && journal.Files.Single().OriginalFileName == "original.mkv" && journal.Files.Single().NewPath == destination && journal.Files.Single().NewFileName == "New title.mkv", "rename history persists original and new paths and filenames outside the media folder");
        var final = Path.Combine(first.Folder, "Final title.mkv"); rename.Execute(new[] { new FileRename(destination, final) });
        check(Directory.GetFiles(histories, "*.json").Length == 2 && File.Exists(final), "successive renames retain each path mapping without overwriting the original history");
        var badNfo = Path.Combine(first.Folder, "bad.nfo"); File.WriteAllText(badNfo, "invalid XML"); var badTarget = Path.Combine(first.Folder, "Season 01", "bad.nfo");
        var failed = false; try { rename.Execute(new[] { new FileRename(badNfo, badTarget) }); } catch (AggregateException) { failed = true; }
        var failure = Directory.GetFiles(histories, "*.json").Select(p => JsonSerializer.Deserialize<RenameHistoryRecord>(File.ReadAllText(p))!).Single(r => r.Files.Any(f => f.OriginalPath == badNfo));
        check(failed && failure.Status == "RolledBack" && File.ReadAllText(badNfo) == "invalid XML" && !File.Exists(badTarget), "failed rename restores original files and records rollback rather than falsely claiming success");
        var blocked = Path.Combine(temporary, "blocked-history"); File.WriteAllText(blocked, "file"); var rejected = false;
        try { new LibraryRenameService(blocked).Execute(new[] { new FileRename(final, destination) }); } catch (IOException) { rejected = true; }
        check(rejected && File.Exists(final) && !File.Exists(destination), "unwritable rename history fails before moving any media");
        check(!Directory.EnumerateFiles(histories).Any(p => p.EndsWith(".tmp")), "rename journals finish with no temporary files");
    }
}
