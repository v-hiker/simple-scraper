using System.Net;
using System.Text;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class SeasonWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        LibraryMedia Fixture(string name, string? subfolder = null)
        {
            var root = Path.Combine(temporary, name); var folder = subfolder == null ? root : Path.Combine(root, subfolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "[Ygm] Grand Blue [01][2160p].mkv");
            File.WriteAllText(path, "isolated third season video");
            File.WriteAllText(Path.ChangeExtension(path, ".zh-CN.ass"), "isolated subtitle");
            File.WriteAllText(Path.ChangeExtension(path, ".nfo"), "<episodedetails><title>Custom episode</title><season>1</season><episode>1</episode><thumb>[Ygm] Grand Blue [01][2160p]-thumb.jpg</thumb></episodedetails>");
            File.WriteAllText(Path.ChangeExtension(path, null) + "-thumb.jpg", "isolated still");
            var doc = new MetadataDocument { Provider = "Bangumi", Id = "569116", Title = "碧蓝之海 第三季", Date = "2026-07-01", Overview = "Third season plot", PosterUrl = "https://test/third-poster",
                Seasons = new() { new(1, "碧蓝之海 第三季", "Third season plot", "2026-07-01", "https://test/third-poster") },
                Episodes = new() { new("1704888", 1, 1, "第一话", "", "", 24, "") } };
            var key = EpisodeMatching.Key(doc, LibraryMediaKind.Series);
            return new() { Folder = root, Title = "碧蓝之海", Kind = LibraryMediaKind.Series,
                Files = new() { new() { Path = path, Season = 3, Episode = 1 } },
                Documents = new() { [key] = doc }, Matches = new() { [3] = new(new("Bangumi", doc.Id, doc.Title, "", 2026, "", doc.PosterUrl), doc) },
                EpisodeBindings = new() { [path] = new(key, doc.Episodes.Single(), 3, 1) } };
        }
        var rename = new LibraryRenameService();
        var output = new LibraryOutputService(new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("third season poster")) }))));
        foreach (var profile in new[] { MediaOutputProfile.Jellyfin, MediaOutputProfile.Emby })
        {
            var media = Fixture("season-third-" + profile.Name);
            var original = media.Files.Single().Path;
            var group = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, profile).Single();
            var seasonFolder = Path.Combine(media.Folder, "Season 03");
            check(group.State == RenamePreviewState.Changed && group.Selected && group.Operations.Count == 4 && group.Operations.All(p => Path.GetDirectoryName(p.Destination) == seasonFolder), profile.Name + " third season preview moves video and all companions to Season 03");
            check(File.Exists(original) && !Directory.Exists(seasonFolder), "season preview is read-only even when the show's root is a season name");
            Directory.CreateDirectory(seasonFolder);
            var destination = group.Operations.Single(p => p.Source == original).Destination;
            File.WriteAllText(destination, "occupied");
            check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, profile).Single().State == RenamePreviewState.Conflict && File.ReadAllText(original) == "isolated third season video", "occupied season target disables the group without touching source");
            File.Delete(destination);
            rename.Execute(group.Operations);
            check(File.ReadAllText(destination) == "isolated third season video" && !File.Exists(original) && File.ReadAllText(Path.ChangeExtension(destination, ".zh-CN.ass")) == "isolated subtitle", "season organization retains video and subtitle bytes");
            var nfo = XDocument.Load(Path.ChangeExtension(destination, ".nfo")).Root!;
            check(nfo.Element("season")?.Value == "3" && nfo.Element("episode")?.Value == "1" && nfo.Element("title")?.Value == "Custom episode" && nfo.Element("thumb")?.Value == Path.GetFileNameWithoutExtension(destination) + "-thumb.jpg", "regular-season organization synchronizes output numbering and image reference, retaining custom title");
            media.Files.Single().Path = destination;
            var binding = media.EpisodeBindings[original]; media.EpisodeBindings.Clear(); media.EpisodeBindings[destination] = binding;
            await output.ScrapeAsync(media, profile, false, seasonOnly: 3);
            var seasonNfo = XDocument.Load(Path.Combine(seasonFolder, "season.nfo")).Root!;
            check(seasonNfo.Element("seasonnumber")?.Value == "3" && seasonNfo.Element("title")?.Value == "碧蓝之海 第三季" && seasonNfo.Element("plot")?.Value == "Third season plot" && seasonNfo.Element("thumb")?.Value == "poster.jpg", "Bangumi subject source season 1 exports confirmed local season 3 information");
            check(File.ReadAllText(Path.Combine(seasonFolder, "poster.jpg")) == "third season poster" && rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, profile).Single().State == RenamePreviewState.Unchanged, "season poster is exported beside NFO and repeated rename is idempotent");
            var scanned = new LibraryScanner().Scan(media.Folder, LibraryMediaKind.Series, false).Single();
            check(scanned.Files.Single().Season == 3 && scanned.SeasonNfoPaths.ContainsKey(3) && scanned.SeasonArtworkPaths.ContainsKey(3), "rescan discovers S03 video, season information and poster");
            var flat = Fixture("season-scrape-first-" + profile.Name);
            File.WriteAllText(Path.Combine(flat.Folder, "season03-poster.png"), "custom legacy poster");
            await output.ScrapeAsync(flat, profile, false, seasonOnly: 3);
            check(File.Exists(flat.Files.Single().Path) && File.Exists(Path.Combine(flat.Folder, "Season 03", "season.nfo")) && File.ReadAllText(Path.Combine(flat.Folder, "Season 03", "poster.png")) == "custom legacy poster", "scrape-first creates season assets, retains legacy PNG art and never moves videos");
            rename.Execute(rename.Preview(flat, LibraryRenameService.EpisodeTemplate, profile: profile));
            check(Directory.GetFiles(Path.Combine(flat.Folder, "Season 03"), "*.mkv").Length == 1 && XDocument.Load(Path.Combine(flat.Folder, "Season 03", "season.nfo")).Root?.Element("thumb")?.Value == "poster.png", "rename after scraping shares the same season directory without losing assets");
        }
        foreach (var name in new[] { "Season 3", "Season 03", "第三季" })
        {
            var existing = Fixture("season-existing-" + name, name);
            check(rename.Preview(existing, LibraryRenameService.EpisodeTemplate).All(p => Path.GetDirectoryName(p.Destination) == Path.Combine(existing.Folder, name)), "valid existing season folder is retained: " + name);
        }
        var rollback = Fixture("season-rollback");
        File.WriteAllText(Path.ChangeExtension(rollback.Files.Single().Path, ".nfo"), "<broken>");
        var failed = false;
        try { rename.Execute(rename.Preview(rollback, LibraryRenameService.EpisodeTemplate)); } catch (AggregateException) { failed = true; }
        check(failed && File.Exists(rollback.Files.Single().Path) && File.ReadAllText(Path.ChangeExtension(rollback.Files.Single().Path, ".nfo")) == "<broken>" && !Directory.Exists(Path.Combine(rollback.Folder, "Season 03")), "failed regular-season move restores original files and removes newly created empty season directory");
        var pending = Fixture("season-pending"); pending.EpisodeBindings.Clear(); pending.Matches.Clear();
        check(rename.BuildPreview(pending, LibraryRenameService.EpisodeTemplate).Single().Operations.Count == 0, "unconfirmed regular-season files are never organized");
    }
}
