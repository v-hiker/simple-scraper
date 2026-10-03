using System.Text;
using System.Text.Json;
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
    private static string StorePath => Path.Combine(AppDataPaths.Root, "library-state.json");
    private static readonly object WriteLock = new();
    public static LibraryStateStore Load()
    {
        if (!File.Exists(StorePath)) return new();
        var state = JsonSerializer.Deserialize<LibraryStateStore>(File.ReadAllText(StorePath)) ?? new();
        state.NormalizeLibraryProfiles();
        return state;
    }
    public void NormalizeLibraryProfiles() => Libraries = Libraries.Select(l => l with { OutputProfile = MediaOutputProfile.ForLibrary(l, Profile).Name }).ToList();
    public void Save()
    {
        lock (WriteLock)
        {
        Directory.CreateDirectory(AppDataPaths.Root);
        var temp = StorePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, StorePath, true);
        }
    }
}
