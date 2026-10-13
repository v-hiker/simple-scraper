using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleScraper.Models;

namespace SimpleScraper.Services;
public sealed class LibraryStateStore
{
    public List<MediaLibrary> Libraries { get; set; } = new();
    public Dictionary<string, LibraryMediaKind> MediaKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastLibraryKey { get; set; } = "";
    public Dictionary<string, Dictionary<int, MetadataMatch>> Matches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Profile { get; set; } = "Jellyfin";
    public List<LibraryColumnPreference> Columns { get; set; } = LibraryColumns.Normalize(null);
    public string SortColumn { get; set; } = "title";
    public bool SortDescending { get; set; }
    public List<string> ExpandedRows { get; set; } = new();
    public Dictionary<string, ManualFileMapping> FileMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MetadataDocument> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EpisodeBinding> EpisodeBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaMetadataEdits> MetadataEdits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore]
    public string FilePath { get; private set; } = AppDataPaths.PathFor("library-state.json");
    private static readonly object WriteLock = new();
    public static LibraryStateStore Load()
    {
        var path = AppDataPaths.PathFor("library-state.json");
        if (!File.Exists(path)) return new() { FilePath = path };
        var state = Read(path);
        state.FilePath = path;
        state.NormalizeLibraryProfiles();
        return state;
    }
    private static LibraryStateStore Read(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize<LibraryStateStore>(File.ReadAllText(path, Encoding.UTF8))
                ?? throw new InvalidDataException("The library state contains no object: " + path);
            if (state.Libraries is null || state.MediaKinds is null || state.Matches is null || state.FileMappings is null || state.Documents is null || state.EpisodeBindings is null || state.MetadataEdits is null || state.ExpandedRows is null)
                throw new InvalidDataException("The library state has invalid records: " + path);
            return state;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The library state is invalid JSON: " + path + ". Restore that file before saving changes.", error);
        }
    }
    public void NormalizeLibraryProfiles() => Libraries = Libraries.Select(l => l with { OutputProfile = MediaOutputProfile.ForLibrary(l, Profile).Name }).ToList();
    private static void ValidateExistingFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The library state contains no object: " + path);
            foreach (var name in new[] { "Libraries", "ExpandedRows" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The library state has invalid records: " + path);
            foreach (var name in new[] { "MediaKinds", "Matches", "FileMappings", "Documents", "EpisodeBindings", "MetadataEdits" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The library state has invalid records: " + path);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The library state is invalid JSON: " + path + ". Restore that file before saving changes.", error);
        }
    }
    public void Save()
    {
        lock (WriteLock)
        {
            if (File.Exists(FilePath)) ValidateExistingFile(FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                    writer.Flush(); stream.Flush(true);
                }
                File.Move(temp, FilePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
