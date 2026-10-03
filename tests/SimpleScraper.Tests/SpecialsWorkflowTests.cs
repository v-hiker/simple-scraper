using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class SpecialsWorkflowTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var root = Path.Combine(temporary, "Specials organization"); var season = Path.Combine(root, "Season 01"); Directory.CreateDirectory(season);
        var source = Path.Combine(season, "S01E11.5.mkv"); var nfo = Path.ChangeExtension(source, ".nfo");
        File.WriteAllText(source, "special bytes"); File.WriteAllText(nfo, "<episodedetails><title>Custom title</title><season>1</season><episode>11</episode><thumb>S01E11.5-thumb.jpg</thumb><actor><thumb>cast.jpg</thumb></actor><fileinfo><codec>hevc</codec></fileinfo></episodedetails>");
        File.WriteAllText(Path.ChangeExtension(source, ".zh-CN.ass"), "subtitle bytes"); File.WriteAllText(Path.ChangeExtension(source, null) + "-thumb.jpg", "still bytes"); File.WriteAllText(Path.Combine(season, "cast.jpg"), "shared cast");
        File.WriteAllText(Path.Combine(season, "S01E12.mkv"), "regular bytes"); File.WriteAllText(Path.Combine(season, "NCOP01.mkv"), "opening bytes");
        var file = new LocalMediaFile { Path = source, Season = 0, Episode = 3, NfoPath = nfo };
        var doc = new MetadataDocument { Provider = "Bangumi", Id = "subject", Title = "特别篇测试", Episodes = new() { new("sp", 0, 1, "前半总集篇", "", "", 0, "", "11.5") } };
        var key = EpisodeMatching.Key(doc, LibraryMediaKind.Series);
        var media = new LibraryMedia { Folder = root, Title = doc.Title, Kind = LibraryMediaKind.Series, Files = new() { file }, Documents = new() { [key] = doc }, EpisodeBindings = new() { [source] = new(key, doc.Episodes.Single(), 0, 3) }, Matches = new() { [0] = new(new(doc.Provider, doc.Id, doc.Title, "", 0, "", ""), doc) } };
        var rename = new LibraryRenameService();
        var jelly = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, MediaOutputProfile.Jellyfin).Single();
        var emby = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, MediaOutputProfile.Emby).Single();
        check(jelly.Operations.Count == 4 && jelly.Operations.All(p => Path.GetDirectoryName(p.Destination) == Path.Combine(root, "Season 00")), "Jellyfin preview organizes reviewed specials and all basename companions into Season 00");
        check(emby.Operations.All(p => Path.GetDirectoryName(p.Destination) == Path.Combine(root, "Specials")), "Emby preview organizes specials into its supported Specials folder");
        check(!Directory.Exists(Path.Combine(root, "Season 00")) && File.Exists(source), "preview and source matching do not create directories or move media");
        check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, organizeSpecials: false).Single().Operations.All(p => Path.GetDirectoryName(p.Destination) == season), "organizing can be disabled while retaining reviewed S00 filenames");
        Directory.CreateDirectory(Path.Combine(root, "Season 00")); var target = jelly.Operations.Single(p => p.Source == source).Destination; File.WriteAllText(target, "occupied special");
        check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate).Single().State == RenamePreviewState.Conflict, "existing target in a different folder disables that special group before execution");
        File.Delete(target); Directory.Delete(Path.Combine(root, "Season 00")); File.WriteAllText(Path.Combine(root, "Season 00"), "blocked folder");
        check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate).Single().State == RenamePreviewState.Conflict, "a file occupying the specials directory is reported as a conflict");
        File.Delete(Path.Combine(root, "Season 00")); rename.Execute(jelly.Operations);
        check(File.ReadAllText(target) == "special bytes" && !File.Exists(source) && File.ReadAllText(Path.ChangeExtension(target, ".zh-CN.ass")) == "subtitle bytes", "execution creates the special directory and moves video and subtitles without changing bytes");
        var movedNfo = XDocument.Load(Path.ChangeExtension(target, ".nfo")).Root!;
        check(movedNfo.Element("season")?.Value == "0" && movedNfo.Element("episode")?.Value == "3" && movedNfo.Element("title")?.Value == "Custom title" && movedNfo.Element("fileinfo") != null, "moving a reviewed special synchronizes NFO output numbering while preserving other fields");
        check(movedNfo.Element("thumb")?.Value == Path.GetFileNameWithoutExtension(target) + "-thumb.jpg" && movedNfo.Element("actor")?.Element("thumb")?.Value == "../Season 01/cast.jpg", "moved NFO references renamed stills and rebases references to unmoved shared images");
        check(File.ReadAllText(Path.Combine(season, "S01E12.mkv")) == "regular bytes" && File.ReadAllText(Path.Combine(season, "NCOP01.mkv")) == "opening bytes", "special-only organization leaves regular episodes and NCOP untouched");
        file.Path = target; media.EpisodeBindings = new() { [target] = new(key, doc.Episodes.Single(), 0, 3) };
        check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate).Single().State == RenamePreviewState.Unchanged, "already organized and named specials are idempotent");
        var rescanned = new LibraryScanner().Scan(root, LibraryMediaKind.Series, false).Single();
        check(rescanned.Files.Single(f => f.Path == target).Season == 0 && rescanned.Files.Single(f => f.Path == target).Episode == 3, "rescan recognizes organized S00 output numbering");
        var migration = rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate, MediaOutputProfile.Emby).Single();
        check(migration.State == RenamePreviewState.Changed && migration.ProposedName == file.Name, "folder-only changes remain selectable even when the basename is unchanged");
        rename.Execute(migration.Operations);
        check(File.Exists(Path.Combine(root, "Specials", file.Name)), "changing export profiles can move an already named special without a filename change");
        var rollbackRoot = Path.Combine(temporary, "Specials rollback"); Directory.CreateDirectory(rollbackRoot);
        var rollbackVideo = Path.Combine(rollbackRoot, "sp.mkv"); var rollbackNfo = Path.Combine(rollbackRoot, "sp.nfo"); File.WriteAllText(rollbackVideo, "rollback bytes"); File.WriteAllText(rollbackNfo, "<broken>");
        var failed = false; try { rename.Execute(new[] { new FileRename(rollbackVideo, Path.Combine(rollbackRoot, "Season 00", "sp.mkv")), new FileRename(rollbackNfo, Path.Combine(rollbackRoot, "Season 00", "sp.nfo")) }); } catch (AggregateException) { failed = true; }
        check(failed && File.ReadAllText(rollbackVideo) == "rollback bytes" && File.ReadAllText(rollbackNfo) == "<broken>" && !Directory.Exists(Path.Combine(rollbackRoot, "Season 00")), "a post-move NFO failure restores originals and removes only newly created empty directories");
        var pending = new LocalMediaFile { Path = Path.Combine(season, "S01E12.mkv"), Season = 0, Episode = 0, NeedsReview = true, Exclusion = "review" };
        media.Files = new() { pending }; check(rename.BuildPreview(media, LibraryRenameService.EpisodeTemplate).Single().Operations.Count == 0, "unknown or unconfirmed zero-season files are not moved as specials");
    }
}
