namespace SimpleScraper.Services;

/// <summary>Read-only operations used by a scan; never retained between scans.</summary>
public interface ILibraryScanFileSystem
{
    bool DirectoryExists(string path);
    LibraryDirectoryInventory ReadDirectory(string path, CancellationToken cancellation);
    Stream OpenRead(string path);
}

/// <summary>One directory listing, including paths needed for metadata and artwork.</summary>
public sealed class LibraryDirectoryInventory
{
    private readonly HashSet<string> filePaths;
    public string Folder { get; }
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Directories { get; }

    public LibraryDirectoryInventory(string folder, IEnumerable<string> files, IEnumerable<string> directories)
    {
        Folder = Path.GetFullPath(folder);
        Files = Array.AsReadOnly(files.ToArray());
        Directories = Array.AsReadOnly(directories.ToArray());
        filePaths = new(Files, StringComparer.OrdinalIgnoreCase);
    }

    public bool ContainsFile(string path) => filePaths.Contains(path);
    public string? ExistingFile(string name)
    {
        var path = Path.Combine(Folder, name);
        return ContainsFile(path) ? path : null;
    }
}

public sealed class PhysicalLibraryScanFileSystem : ILibraryScanFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public LibraryDirectoryInventory ReadDirectory(string path, CancellationToken cancellation)
    {
        var files = new List<string>();
        var directories = new List<string>();
        // FileSystemInfo's type comes from enumeration data. No per-entry stat,
        // and no second directory enumeration is needed to find subdirectories.
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo) directories.Add(entry.FullName);
            else files.Add(entry.FullName);
        }
        return new(path, files, directories);
    }

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
