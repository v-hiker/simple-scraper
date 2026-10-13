using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public sealed record LibrarySnapshot(int Version, DateTimeOffset SavedAt, List<LibraryMedia> Media);

// Physical directory data is independent of matching records. Loading a snapshot never touches its media paths.
public sealed class LibrarySnapshotStore
{
    private readonly string _directory;
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions Json = new() { IgnoreReadOnlyProperties = true };
    public LibrarySnapshotStore(string? directory = null) => _directory = directory ?? Path.Combine(AppDataPaths.Root, "library-cache");
    public static string Key(MediaLibrary library)
    {
        var identity = Path.GetFullPath(library.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant() + "|" + library.Kind + "|" + library.IsRoot;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    private string FilePath(MediaLibrary library) => Path.Combine(_directory, Key(library) + ".json");
    public LibrarySnapshot? Load(MediaLibrary library)
    {
        var path = FilePath(library); if (!File.Exists(path)) return null;
        var snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(File.ReadAllText(path, Encoding.UTF8), Json);
        if (snapshot?.Version != 1) throw new InvalidDataException(L.Text("Library cache is unreadable. Click Refresh library to rebuild it."));
        return snapshot;
    }
    public void Save(MediaLibrary library, LibrarySnapshot snapshot)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(_directory); var path = FilePath(library);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json), new UTF8Encoding(false)); File.Move(temp, path, true);
        }
    }
    public void Remove(MediaLibrary library) { lock (WriteLock) File.Delete(FilePath(library)); }
    public static List<LibraryMedia> CopyMedia(LibrarySnapshot snapshot, LibraryMediaKind? libraryKind = null)
    {
        var copy = JsonSerializer.Deserialize<List<LibraryMedia>>(JsonSerializer.Serialize(snapshot.Media, Json), Json) ?? new();
        // Upgrade old auto-detected movie entries using saved filenames only,
        // including offline libraries. Explicit movie libraries stay movies.
        if (libraryKind is LibraryMediaKind.Auto or LibraryMediaKind.Series) LibraryScanner.CorrectCachedAutoDetection(copy, libraryKind == LibraryMediaKind.Auto);
        foreach (var media in copy) ExtraMedia.ApplyHints(media);
        return copy;
    }
    public static void ApplyRecords(IEnumerable<LibraryMedia> media, LibraryStateStore state)
    {
        foreach (var m in media)
        {
            if (state.MediaKinds.TryGetValue(m.Folder, out var kind) && kind is LibraryMediaKind.Movie or LibraryMediaKind.Series && m.Kind != kind) LibraryScanner.SetMediaKind(m, kind);
            m.Matches = state.Matches.TryGetValue(m.Folder, out var matches) ? new(matches) : new();
            m.Edits = state.MetadataEdits.GetValueOrDefault(m.Folder) ?? new();
            m.Documents.Clear(); m.EpisodeBindings.Clear();
            foreach (var f in m.Files)
            {
                if (state.FileMappings.TryGetValue(f.Path, out var map))
                {
                    // Earlier versions saved automatic fractional exclusions as invalid 0/0 mappings.
                    if (!(f.NeedsReview && map.Excluded && map.Episode <= 0 && !map.ExplicitExclusion))
                    {
                        f.Season = map.Season; f.Episode = map.Episode; f.Exclusion = map.Excluded ? L.Text("Manually excluded") : ""; f.NeedsReview = false; f.ManuallyExcluded = map.Excluded;
                    }
                }
                if (state.EpisodeBindings.TryGetValue(f.Path, out var b)) m.EpisodeBindings[f.Path] = b;
            }
            foreach (var doc in m.Matches.Values.Select(v => v.Document)) m.Documents[EpisodeMatching.Key(doc, m.Kind)] = doc;
            foreach (var key in m.EpisodeBindings.Values.Select(b => b.SourceKey).Distinct()) if (state.Documents.TryGetValue(key, out var document)) m.Documents[key] = document;
        }
    }
}
