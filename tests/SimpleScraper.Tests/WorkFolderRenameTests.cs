using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class WorkFolderRenameTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var parent = Path.Combine(temporary, "root-rename"); var source = Path.Combine(parent, "第三季"); Directory.CreateDirectory(source);
        var video = Path.Combine(source, "S03E01.mkv"); File.WriteAllText(video, "video");
        var poster = Path.Combine(source, "poster.jpg"); File.WriteAllText(poster, "poster");
        var excluded = Path.Combine(source, "unmatched.mkv"); File.WriteAllText(excluded, "excluded");
        var nfo = Path.ChangeExtension(video, ".nfo"); File.WriteAllText(nfo, "<episodedetails><season>3</season><episode>1</episode><thumb>poster.jpg</thumb></episodedetails>");
        File.WriteAllText(Path.Combine(source, "tvshow.nfo"), new XElement("tvshow", new XElement("thumb", poster)).ToString());
        var doc = new MetadataDocument { Provider = "TMDB", Id = "11", Title = "Another Show", Date = "2018-01-01", Episodes = new() { new("31", 3, 1, "First", "", "", 20, "") } };
        var subject = new MetadataDocument { Provider = "Bangumi", Id = "22", Title = "Another Show 第三季", Date = "2026-01-01" };
        var match = new MetadataMatch(new("TMDB", "11", doc.Title, "", 2018, "", ""), doc);
        var file = new LocalMediaFile { Path = video, Season = 3, Episode = 1, NfoPath = nfo };
        var media = new LibraryMedia { Folder = source, Kind = LibraryMediaKind.Series, Title = "第三季", Matches = new() { [-1] = new(new("Bangumi", "22", subject.Title, "", 2026, "", ""), subject), [3] = match }, Files = new() { file, new() { Path = excluded, Exclusion = "excluded" } }, PosterPath = poster, NfoPath = Path.Combine(source, "tvshow.nfo") };
        check(LibraryRenameService.WorkFolderName(media) == "Another Show (2018)", "default root template uses whole-series title and first-release year");
        check(LibraryRenameService.WorkFolderName(media, "{Title}") == "Another Show", "custom folder template can omit year");
        doc.OriginalTitle = "Original Show";
        check(LibraryRenameService.WorkFolderName(media, "[{Year}] {OriginalTitle}") == "[2018] Original Show", "folder template supports original title and custom token order");
        var priorDate = doc.Date; doc.Date = "";
        check(LibraryRenameService.WorkFolderName(media) == "Another Show", "missing year leaves no empty parentheses"); doc.Date = priorDate;
        bool badTemplate = false; try { LibraryRenameService.WorkFolderName(media, "{Episode}"); } catch (ArgumentException) { badTemplate = true; }
        check(badTemplate, "episode-only tokens are rejected in work folder template");
        check(JsonSerializer.Deserialize<AppConfig>("{}")!.WorkFolderRenameTemplate == LibraryRenameService.WorkFolderTemplate, "old settings migrate to the default folder template");
        var config = new ConfigService(); var originalTemplate = config.GetWorkFolderRenameTemplate(); config.SetWorkFolderRenameTemplate("{OriginalTitle} ({Year})");
        check(new ConfigService().GetWorkFolderRenameTemplate() == "{OriginalTitle} ({Year})" && JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(config.FilePath))!.WorkFolderRenameTemplate == "{OriginalTitle} ({Year})", "folder template saves independently and is shared across config instances"); config.SetWorkFolderRenameTemplate(originalTemplate);
        var savedMovieKind = media.Kind; media.Kind = LibraryMediaKind.Movie; check(LibraryRenameService.WorkFolderName(media) == "Another Show (2018)", "movies use the same default title-year folder template"); media.Kind = savedMovieKind;
        media.Edits.Work = new() { Title = "Edited work" }; check(LibraryRenameService.WorkFolderName(media) == "Edited work", "custom work title takes priority for root name"); media.Edits.Work = null;
        var rename = LibraryRenameService.PreviewFolder(media, "Another Show")!;
        check(LibraryRenameService.PreviewFolder(media, "第三季") == null, "same work folder name is unchanged");
        bool invalid = false; try { LibraryRenameService.PreviewFolder(media, "../outside"); } catch (ArgumentException) { invalid = true; }
        check(invalid, "root rename rejects traversal");
        Directory.CreateDirectory(rename.Destination); bool conflict = false; try { LibraryRenameService.Validate(Array.Empty<FileRename>(), rename); } catch (IOException) { conflict = true; }
        check(conflict && File.Exists(video), "existing work directory blocks rename without changing source"); Directory.Delete(rename.Destination);
        var history = Path.Combine(parent, "history");
        var service = new LibraryRenameService(history);
        var plan = service.Preview(media, LibraryRenameService.EpisodeTemplate);
        var destination = LibraryPathMigration.MoveRoot(plan.First(p => p.Source == video).Destination, rename);
        check(Path.GetDirectoryName(destination) == Path.Combine(rename.Destination, "Season 03"), "root plus season move previews final destination");
        var library = new MediaLibrary("Single", source, LibraryMediaKind.Series, false);
        var state = new LibraryStateStore { Libraries = new() { library }, LastLibraryKey = LibrarySnapshotStore.Key(library), Matches = new() { [source] = media.Matches }, MediaKinds = new() { [source] = LibraryMediaKind.Series }, FileMappings = new() { [video] = new(3, 1, false), [excluded] = new(0, 0, true) }, EpisodeBindings = new() { [video] = new("TMDB", doc.Episodes[0], 3, 1) }, ExpandedRows = new() { source, source + "|season:3" }, MetadataEdits = new() { [source] = new() { Episodes = new() { [video] = new() { Title = "Custom episode" } } } } };
        service.Execute(plan, rename);
        check(!Directory.Exists(source) && File.ReadAllText(destination) == "video", "root and season rename execute as one operation");
        check(File.ReadAllText(Path.Combine(rename.Destination, "unmatched.mkv")) == "excluded" && File.ReadAllText(Path.Combine(rename.Destination, "poster.jpg")) == "poster", "root rename preserves unselected videos and work artwork");
        var movedNfo = Path.ChangeExtension(destination, ".nfo");
        var relativeThumb = XDocument.Load(movedNfo).Root!.Element("thumb")!.Value;
        check(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(movedNfo)!, relativeThumb))), "episode NFO relative artwork remains valid after root move");
        check(XDocument.Load(Path.Combine(rename.Destination, "tvshow.nfo")).Root!.Element("thumb")!.Value == Path.Combine(rename.Destination, "poster.jpg"), "absolute work artwork reference follows root rename");
        var journal = JsonSerializer.Deserialize<RenameHistoryRecord>(File.ReadAllText(Directory.GetFiles(history).Single()))!;
        check(journal.Status == "Completed" && journal.Folder == rename && journal.Files.Any(f => f.NewPath == destination), "journal stores original root and final file destinations");
        LibraryPathMigration.Apply(state, plan, rename); LibraryPathMigration.Apply(media, plan, rename);
        check(state.Matches.ContainsKey(rename.Destination) && !state.Matches.ContainsKey(source), "match records follow work root");
        check(state.FileMappings.ContainsKey(destination) && state.FileMappings.ContainsKey(Path.Combine(rename.Destination, "unmatched.mkv")), "selected and excluded file mappings follow root");
        check(state.EpisodeBindings.ContainsKey(destination) && state.MetadataEdits[rename.Destination].Episodes[destination].Title == "Custom episode", "bindings and custom metadata follow final episode path");
        check(state.ExpandedRows.Contains(rename.Destination + "|season:3") && state.Libraries[0].Path == rename.Destination && state.LastLibraryKey == LibrarySnapshotStore.Key(state.Libraries[0]), "expanded seasons and single-work library key migrate");
        check(media.Folder == rename.Destination && file.Path == destination && file.NfoPath == movedNfo && media.PosterPath == Path.Combine(rename.Destination, "poster.jpg"), "snapshot media and assets migrate");
        var rollback = Path.Combine(parent, "rollback"); Directory.CreateDirectory(rollback);
        var rollbackVideo = Path.Combine(rollback, "episode.mkv"); var rollbackNfo = Path.Combine(rollback, "episode.nfo"); File.WriteAllText(rollbackVideo, "safe"); File.WriteAllText(rollbackNfo, "broken xml");
        var rollbackFolder = new FolderRename(rollback, Path.Combine(parent, "rollback-destination"));
        bool rolledBack = false;
        try { service.Execute(new[] { new FileRename(rollbackVideo, Path.Combine(rollback, "Season 03", "new.mkv")), new FileRename(rollbackNfo, Path.Combine(rollback, "Season 03", "new.nfo")) }, rollbackFolder); } catch (AggregateException) { rolledBack = true; }
        check(rolledBack && File.ReadAllText(rollbackVideo) == "safe" && File.ReadAllText(rollbackNfo) == "broken xml" && !Directory.Exists(rollbackFolder.Destination), "failure restores original files and work root");
        var only = Path.Combine(parent, "folder-only"); Directory.CreateDirectory(only); File.WriteAllText(Path.Combine(only, "note.txt"), "retain");
        var onlyRename = new FolderRename(only, Path.Combine(parent, "folder-only-new")); service.Execute(Array.Empty<FileRename>(), onlyRename);
        check(File.ReadAllText(Path.Combine(onlyRename.Destination, "note.txt")) == "retain", "work folder can rename even when episode filenames are already correct");
        check(LibraryPathMigration.MoveRoot(source + "-other\\file.mkv", rename) == source + "-other\\file.mkv", "root mapping respects path boundaries");
    }
}
