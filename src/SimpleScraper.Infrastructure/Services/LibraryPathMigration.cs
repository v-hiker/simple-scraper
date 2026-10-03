using SimpleScraper.Models;

namespace SimpleScraper.Services;

// Paths change as one mapping, including files not selected for filename changes.
public static class LibraryPathMigration
{
    public static string MoveRoot(string path, FolderRename? folder)
    {
        if (folder == null || string.IsNullOrEmpty(path)) return path;
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Source));
        if (path.Equals(source, StringComparison.OrdinalIgnoreCase)) return folder.Destination;
        return path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder.Destination, path[(source.Length + 1)..]) : path;
    }
    public static string MovePath(string path, IReadOnlyList<FileRename> files, FolderRename? folder) =>
        MoveRoot(files.FirstOrDefault(f => f.Source.Equals(path, StringComparison.OrdinalIgnoreCase))?.Destination ?? path, folder);
    private static Dictionary<string, T> Map<T>(Dictionary<string, T> values, Func<string, string> map) =>
        values.ToDictionary(p => map(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
    public static void Apply(LibraryStateStore state, IReadOnlyList<FileRename> files, FolderRename? folder)
    {
        string PathMap(string path) => MovePath(path, files, folder);
        state.FileMappings = Map(state.FileMappings, PathMap);
        state.EpisodeBindings = Map(state.EpisodeBindings, PathMap);
        state.MediaKinds = Map(state.MediaKinds, p => MoveRoot(p, folder));
        state.Matches = Map(state.Matches, p => MoveRoot(p, folder));
        state.MetadataEdits = Map(state.MetadataEdits, p => MoveRoot(p, folder));
        foreach (var edits in state.MetadataEdits.Values) edits.Episodes = Map(edits.Episodes, PathMap);
        state.ExpandedRows = state.ExpandedRows.Select(p =>
        {
            var split = p.IndexOf("|season:", StringComparison.Ordinal);
            return split < 0 ? PathMap(p) : MoveRoot(p[..split], folder) + p[split..];
        }).ToList();
        var active = state.Libraries.FirstOrDefault(l => LibrarySnapshotStore.Key(l) == state.LastLibraryKey);
        state.Libraries = state.Libraries.Select(l => l with { Path = MoveRoot(l.Path, folder) }).ToList();
        if (active != null) state.LastLibraryKey = LibrarySnapshotStore.Key(active with { Path = MoveRoot(active.Path, folder) });
    }
    public static void Apply(LibraryMedia media, IReadOnlyList<FileRename> files, FolderRename? folder)
    {
        string PathMap(string path) => MovePath(path, files, folder);
        media.Folder = MoveRoot(media.Folder, folder);
        foreach (var file in media.Files)
        {
            file.Path = PathMap(file.Path);
            if (file.NfoPath != null) file.NfoPath = PathMap(file.NfoPath);
            if (file.ThumbPath != null) file.ThumbPath = PathMap(file.ThumbPath);
        }
        if (media.NfoPath != null) media.NfoPath = PathMap(media.NfoPath);
        if (media.PosterPath != null) media.PosterPath = PathMap(media.PosterPath);
        media.ArtworkPaths = media.ArtworkPaths.ToDictionary(p => p.Key, p => PathMap(p.Value));
        media.SeasonNfoPaths = media.SeasonNfoPaths.ToDictionary(p => p.Key, p => PathMap(p.Value));
        media.SeasonArtworkPaths = media.SeasonArtworkPaths.ToDictionary(p => p.Key, p => p.Value.ToDictionary(a => a.Key, a => PathMap(a.Value)));
        media.EpisodeBindings = Map(media.EpisodeBindings, PathMap);
    }
}
