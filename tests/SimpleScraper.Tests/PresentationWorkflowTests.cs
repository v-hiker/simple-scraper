using System.Text.Json;
using SimpleScraper.Localization;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class PresentationWorkflowTests
{
    public static void Run(Action<bool, string> check)
    {
        var defaultColumns = new LibraryStateStore().Columns;
        var expectedVisible = new[] { "title", "year", "type", "match", "nfo", "poster", "rating", "seasons", "episodes" };
        check(defaultColumns.Where(c => c.Visible).Select(c => c.Id).SequenceEqual(expectedVisible) && LibraryColumns.Normalize(null).Where(c => c.Visible).Select(c => c.Id).SequenceEqual(expectedVisible), "new libraries and reset layouts show season count, episode count and rating alongside the existing default columns");
        foreach (var column in defaultColumns.Where(c => c.Id is "seasons" or "episodes" or "rating")) column.Visible = false;
        defaultColumns.Single(c => c.Id == "title").Width = 295;
        var savedColumns = JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(new LibraryStateStore { Columns = defaultColumns }))!.Columns;
        check(LibraryColumns.Normalize(savedColumns).Select(c => (c.Id, c.Visible, c.Width)).SequenceEqual(defaultColumns.Select(c => (c.Id, c.Visible, c.Width))), "explicitly hidden season count, episode count and rating remain hidden when a saved layout is restored");
        var language = Localizer.Language;
        try
        {
            Localizer.Initialize("zh-CN");
            check(Localizer.Text(LibraryColumns.All.Single(c => c.Id == "episodes").Label) == "集数", "Chinese episode column uses the concise episode-count label");
        }
        finally { Localizer.Initialize(language); }
        check(new AppConfig().DefaultMetadataSources.SequenceEqual(new[] { "TMDB" }) && JsonSerializer.Deserialize<AppConfig>("{}")!.DefaultMetadataSources.SequenceEqual(new[] { "TMDB" }), "new and empty settings keep TMDB as the sole default search source");
        var legacy = JsonSerializer.Deserialize<LibraryStateStore>("""
            {"Profile":"Emby","Libraries":[
                {"Name":"legacy","Path":"C:/test/old","Kind":1,"IsRoot":true},
                {"Name":"explicit","Path":"C:/test/new","Kind":0,"IsRoot":true,"OutputProfile":"Jellyfin"}
            ]}
            """)!;
        legacy.NormalizeLibraryProfiles();
        check(legacy.Libraries[0].OutputProfile == "Emby" && legacy.Libraries[1].OutputProfile == "Jellyfin", "old libraries inherit the previous export profile without overwriting explicit per-library settings");
        var persisted = JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(legacy))!;
        persisted.Profile = "Jellyfin"; persisted.NormalizeLibraryProfiles();
        check(MediaOutputProfile.ForLibrary(persisted.Libraries[0], persisted.Profile) == MediaOutputProfile.Emby && MediaOutputProfile.ForLibrary(persisted.Libraries[1], "Emby") == MediaOutputProfile.Jellyfin, "each library keeps its export profile after serialization and global legacy profile changes");
        check(LibrarySnapshotStore.Key(legacy.Libraries[0]) == LibrarySnapshotStore.Key(legacy.Libraries[0] with { Name = "renamed", OutputProfile = "Jellyfin" }), "changing the library name or export profile reuses its existing scan cache");
        check(MediaOutputProfile.ForLibrary(legacy.Libraries[0] with { OutputProfile = "invalid" }) == MediaOutputProfile.Jellyfin, "unsupported media tool settings safely use the default profile");
        var columns = LibraryColumns.Normalize(null); columns.Single(c => c.Id == "title").Width = 371;
        var hidden = columns.Where(c => !c.Visible).Select(c => (Index: columns.IndexOf(c), c.Id)).ToList();
        var visible = columns.Where(c => c.Visible).Select(c => c.Id).ToList();
        check(LibraryColumns.MoveVisible(columns, 0, visible.Count) && columns.Where(c => c.Visible).Last().Id == "title", "header dragging can move title after other columns while keeping the title visible");
        check(hidden.All(p => columns[p.Index].Id == p.Id) && columns.Single(c => c.Id == "title").Width == 371, "visible header reordering preserves hidden positions and each column's width");
        var order = columns.Select(c => c.Id).ToArray();
        check(!LibraryColumns.MoveVisible(columns, 2, 2) && !LibraryColumns.MoveVisible(columns, 2, 3) && !LibraryColumns.MoveVisible(columns, -1, 0) && columns.Select(c => c.Id).SequenceEqual(order), "same-position and invalid header drops leave the saved layout unchanged");
        var state = new LibraryStateStore { Columns = columns, SortColumn = "title", SortDescending = true };
        var restored = JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(state))!;
        check(LibraryColumns.Normalize(restored.Columns).Select(c => (c.Id, c.Width, c.Visible)).SequenceEqual(columns.Select(c => (c.Id, c.Width, c.Visible))) && restored.SortColumn == "title" && restored.SortDescending, "header width, order and sorting persist together and normalize without undoing the drag");
        for (var from = 0; from < visible.Count; from++) for (var boundary = 0; boundary <= visible.Count; boundary++)
        {
            var layout = LibraryColumns.Normalize(null); var before = layout.Where(c => c.Visible).Select(c => c.Id).ToList(); var expected = before.ToList(); var moving = expected[from]; expected.RemoveAt(from); expected.Insert(boundary > from ? boundary - 1 : boundary, moving);
            LibraryColumns.MoveVisible(layout, from, boundary);
            check(layout.Where(c => c.Visible).Select(c => c.Id).SequenceEqual(expected) && layout.Select(c => c.Id).Distinct().Count() == LibraryColumns.All.Count, $"header insertion boundary {from}→{boundary} preserves all columns in the expected order");
        }
        check(MetadataLinks.Get("TMDB", "65336", LibraryMediaKind.Series)?.AbsoluteUri == "https://www.themoviedb.org/tv/65336" && MetadataLinks.Get("TMDB", "65336", LibraryMediaKind.Movie)?.AbsolutePath == "/movie/65336", "provider links distinguish TV from movies");
        check(MetadataLinks.Get("Bangumi", "211567", LibraryMediaKind.Series)?.AbsoluteUri == "https://bgm.tv/subject/211567" && MetadataLinks.Get("IMDb", "tt5176548", LibraryMediaKind.Series)?.AbsolutePath == "/title/tt5176548/", "confirmed provider IDs link to their canonical public pages");
        check(new[] { "-1", "0", "12/other", "https://other.test", "12?x=y", " 12", "" }.All(id => MetadataLinks.Get("TMDB", id, LibraryMediaKind.Series) == null) && MetadataLinks.Get("unknown", "12", LibraryMediaKind.Series) == null && MetadataLinks.Get("IMDb", "tt1/other", LibraryMediaKind.Series) == null, "malformed and unsupported metadata IDs never create arbitrary web links");
    }
}
