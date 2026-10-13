using System.Text.Json;

namespace SimpleScraper.Services;

/// <summary>Preserves portable data when the executable is first moved to user storage.</summary>
public static class AppDataMigration
{
    private static readonly object Gate = new();

    public static int ImportFromExecutableDirectory()
    {
        // Single-file applications extract dependencies elsewhere; the real executable owns portable data.
        var executable = Environment.ProcessPath;
        return executable is null ? 0 : ImportPortableData(Path.GetDirectoryName(executable)!, AppDataPaths.Root);
    }

    public static int ImportPortableData(string sourceDirectory, string destinationDirectory)
    {
        lock (Gate)
        {
            var source = Path.GetFullPath(sourceDirectory);
            var destination = Path.GetFullPath(destinationDirectory);
            var sourceState = Path.Combine(source, "library-state.json");
            if (SameDirectory(source, destination) || File.Exists(Path.Combine(destination, "library-state.json")) || !File.Exists(sourceState)) return 0;
            if (IsLink(source) || IsLink(sourceState)) return 0;
            using (var document = JsonDocument.Parse(File.ReadAllText(sourceState, System.Text.Encoding.UTF8)))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Portable library state is unreadable. Its original files have been retained.");
            }

            var copied = 0;
            copied += CopyFile(Path.Combine(source, "appsettings.json"), Path.Combine(destination, "appsettings.json"));
            foreach (var relative in new[] { "library-cache", Path.Combine("history", "rename"), Path.Combine("cache", "people"), Path.Combine("cache", "translations") })
                copied += CopyDirectory(Path.Combine(source, relative), Path.Combine(destination, relative));
            // Publish the library list after its supporting records so interrupted imports can resume.
            copied += CopyFile(sourceState, Path.Combine(destination, "library-state.json"));
            return copied;
        }
    }

    private static bool SameDirectory(string first, string second) => Path.TrimEndingDirectorySeparator(first).Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static int CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source) || IsLink(source)) return 0;
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source))
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) copied += CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            copied += CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        return copied;
    }

    private static int CopyFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination) || IsLink(source)) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination)) { return 0; }
            return 1;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
