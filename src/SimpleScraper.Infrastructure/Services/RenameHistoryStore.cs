using System.Text;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public sealed record RenameHistoryEntry(string OriginalPath, string OriginalFileName, string NewPath, string NewFileName);
public sealed record RenameHistoryRecord(DateTimeOffset StartedAt, string Status, List<RenameHistoryEntry> Files)
{
    public FolderRename? Folder { get; init; }
}

public sealed class RenameHistoryStore(string? directory = null)
{
    public static string DefaultDirectory => Path.Combine(AppDataPaths.Root, "history", "rename");
    public string DirectoryPath { get; } = Path.GetFullPath(directory ?? DefaultDirectory);
    public string Begin(IReadOnlyList<FileRename> plan, FolderRename? folder = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var started = DateTimeOffset.UtcNow;
        var path = Path.Combine(DirectoryPath, started.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        Write(path, new(started, "Planned", plan.Select(p => new RenameHistoryEntry(Path.GetFullPath(p.Source), Path.GetFileName(p.Source), LibraryPathMigration.MoveRoot(Path.GetFullPath(p.Destination), folder), Path.GetFileName(p.Destination))).ToList()) { Folder = folder });
        return path;
    }
    public void Finish(string path, string status)
    {
        var record = JsonSerializer.Deserialize<RenameHistoryRecord>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("Rename history is unreadable.");
        Write(path, record with { Status = status });
    }
    private static void Write(string path, RenameHistoryRecord record)
    {
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
