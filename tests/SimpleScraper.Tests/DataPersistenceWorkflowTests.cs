using System.Text;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class DataPersistenceWorkflowTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var previousHome = Environment.GetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME");
        var previousWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            var root = Path.Combine(temporary, "upgrade-user-data");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", root);
            var firstVersion = Path.Combine(temporary, "release-1");
            var secondVersion = Path.Combine(temporary, "release-2");
            Directory.CreateDirectory(firstVersion); Directory.CreateDirectory(secondVersion);
            Environment.CurrentDirectory = firstVersion;
            var mediaFolder = Path.Combine(temporary, "upgrade-media"); Directory.CreateDirectory(mediaFolder);
            var original = Path.Combine(mediaFolder, "Episode.S01E01.mkv"); File.WriteAllText(original, "fixture");
            var library = new MediaLibrary("Persisted library", mediaFolder, LibraryMediaKind.Series, false) { OutputProfile = "Jellyfin" };
            new ConfigService().SetUiTheme("dark");
            new LibraryStateStore { Libraries = new() { library }, LastLibraryKey = LibrarySnapshotStore.Key(library) }.Save();
            new LibrarySnapshotStore().Save(library, new(1, DateTimeOffset.UtcNow, new LibraryScanner().Scan(library)));
            var journal = new RenameHistoryStore().Begin(new[] { new FileRename(original, Path.Combine(mediaFolder, "Episode 01.mkv")) });
            new RenameHistoryStore().Finish(journal, "Completed");

            Environment.CurrentDirectory = secondVersion;
            var state = LibraryStateStore.Load();
            check(new ConfigService().GetUiTheme() == "dark" && state.Libraries.Single() == library && state.LastLibraryKey == LibrarySnapshotStore.Key(library), "settings and library selection survive launching the next version from another working directory");
            check(new LibrarySnapshotStore().Load(library)!.Media.Single().Files.Single().Path == original && File.Exists(journal) && new RenameHistoryStore().DirectoryPath == Path.GetDirectoryName(journal), "scan snapshots and rename history remain in the same user storage across release directories");
            check(new ConfigService().FilePath == Path.Combine(root, "appsettings.json") && new ActorImageCache().DirectoryPath == Path.Combine(root, "cache", "people") && !Directory.EnumerateFileSystemEntries(firstVersion).Any() && !Directory.EnumerateFileSystemEntries(secondVersion).Any(), "workflow state is written outside both executable working directories");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", Path.Combine(temporary, "another-isolated-home"));
            state.Save();
            check(File.Exists(Path.Combine(root, "library-state.json")) && !Directory.Exists(AppDataPaths.Root), "loaded library state keeps its original file path when a process environment override changes");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", null);
            check(AppDataPaths.Root == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScraper"), "the default data directory contains no application version or executable directory");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", "relative-data");
            var rejectedRelative = false;
            try { _ = AppDataPaths.Root; } catch (ArgumentException) { rejectedRelative = true; }
            check(rejectedRelative, "relative data overrides cannot split user history when the working directory changes");

            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", Path.Combine(temporary, "protected-state"));
            Directory.CreateDirectory(AppDataPaths.Root);
            var statePath = AppDataPaths.PathFor("library-state.json");
            foreach (var unreadable in new[] { "{invalid", "null", "{\"Libraries\":null}" })
            {
                File.WriteAllText(statePath, unreadable, new UTF8Encoding(false));
                var loadRejected = false; var saveRejected = false;
                try { _ = LibraryStateStore.Load(); } catch (InvalidDataException) { loadRejected = true; }
                try { new LibraryStateStore().Save(); } catch (InvalidDataException) { saveRejected = true; }
                check(loadRejected && saveRejected && File.ReadAllText(statePath) == unreadable, "unreadable library state is reported and protected from replacement by an empty fallback");
            }
            check(!Directory.EnumerateFiles(AppDataPaths.Root, "*.tmp").Any(), "rejected state writes preserve the original without staging files");

            var portable = Path.Combine(temporary, "portable-release");
            var migrated = Path.Combine(temporary, "migrated-user-data");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", portable);
            new LibraryStateStore { Libraries = new() { library } }.Save();
            new ConfigService().SetUiTheme("light");
            new LibrarySnapshotStore().Save(library, new(1, DateTimeOffset.UtcNow, new LibraryScanner().Scan(library)));
            var portableJournal = new RenameHistoryStore().Begin(Array.Empty<FileRename>());
            var portablePeople = Path.Combine(new ActorImageCache().DirectoryPath, "tmdb", "1"); Directory.CreateDirectory(portablePeople);
            File.WriteAllText(Path.Combine(portablePeople, "person.json"), "{\"Id\":1}");
            File.WriteAllBytes(Path.Combine(portablePeople, "portrait.jpg"), new byte[] { 255, 216, 255, 217 });
            File.WriteAllText(Path.Combine(portable, "SimpleScraper.dll"), "binary fixture");
            File.WriteAllText(Path.Combine(portable, "unrelated.json"), "private unrelated fixture");
            File.WriteAllText(Path.Combine(portable, "library-cache", "interrupted.tmp"), "incomplete fixture");
            Directory.CreateDirectory(migrated);
            var migratedSettings = Path.Combine(migrated, "appsettings.json");
            new ConfigService(migratedSettings).SetUiTheme("dark");
            var settingsBefore = File.ReadAllBytes(migratedSettings);
            var sourcesBefore = Directory.EnumerateFiles(portable, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            check(AppDataMigration.ImportPortableData(portable, migrated) > 0, "portable library records import into fixed user storage on first launch");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", migrated);
            check(LibraryStateStore.Load().Libraries.Single() == library && new LibrarySnapshotStore().Load(library)!.Media.Count == 1 && File.Exists(Path.Combine(new RenameHistoryStore().DirectoryPath, Path.GetFileName(portableJournal))) && File.Exists(Path.Combine(new ActorImageCache().DirectoryPath, "tmdb", "1", "portrait.jpg")), "portable import retains library selection, snapshots, rename journals and shared portraits");
            check(settingsBefore.SequenceEqual(File.ReadAllBytes(migratedSettings)) && sourcesBefore.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "portable import retains existing user settings and every original source file");
            check(!File.Exists(Path.Combine(migrated, "SimpleScraper.dll")) && !File.Exists(Path.Combine(migrated, "unrelated.json")) && !Directory.EnumerateFiles(migrated, "*.tmp", SearchOption.AllDirectories).Any(), "portable import excludes binaries, unrelated files and interrupted cache writes");
            var migratedState = File.ReadAllBytes(AppDataPaths.PathFor("library-state.json"));
            check(AppDataMigration.ImportPortableData(portable, migrated) == 0 && migratedState.SequenceEqual(File.ReadAllBytes(AppDataPaths.PathFor("library-state.json"))), "later launches preserve the canonical library without mixing another portable data set");
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", Path.Combine(temporary, "process-home"));
            Environment.CurrentDirectory = portable;
            check(AppDataMigration.ImportFromExecutableDirectory() == 0 && !Directory.Exists(AppDataPaths.Root), "changing the working directory does not make migration discover unrelated portable data");
        }
        finally
        {
            Environment.CurrentDirectory = previousWorkingDirectory;
            Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", previousHome);
        }
    }
}
