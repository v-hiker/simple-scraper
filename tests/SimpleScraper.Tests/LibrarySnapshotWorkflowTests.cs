using SimpleScraper.Models;
using SimpleScraper.Services;

static class LibrarySnapshotWorkflowTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var folder = Path.Combine(temporary, "Snapshot-media"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "S01E01.mkv"), "isolated fixture"); File.WriteAllText(Path.Combine(folder, "S01E11.5.mkv"), "isolated fixture");
        var library = new MediaLibrary("snapshot", folder, LibraryMediaKind.Series, false); var scanner = new LibraryScanner();
        var store = new LibrarySnapshotStore(Path.Combine(temporary, "Snapshot-cache")); var raw = scanner.Scan(folder, library.Kind, false);
        var pending = raw.Single().Files.Single(f => f.Name == "S01E11.5.mkv");
        check(pending.NeedsReview && pending.Season == 1 && pending.SourceNumber == "11.5" && pending.Episode == 0, "fractional filename retains original numbering for review without becoming regular episode 11");
        store.Save(library, new(1, DateTimeOffset.UtcNow, raw));
        File.WriteAllText(Path.Combine(folder, "S01E02.mkv"), "added after scanning");
        var loaded = new LibrarySnapshotStore(Path.Combine(temporary, "Snapshot-cache")).Load(library)!;
        check(loaded.Media.Single().Files.Count == 2, "loading a persisted snapshot does not discover files added since the last refresh");
        var offline = folder + "-offline"; Directory.Move(folder, offline);
        check(store.Load(library)!.Media.Single().Files.Count == 2, "cached library loads while the original media directory is offline");
        var failed = false; try { scanner.Scan(folder, library.Kind, false); } catch (DirectoryNotFoundException) { failed = true; }
        check(failed && store.Load(library)!.Media.Single().Files.Count == 2, "failed explicit refresh leaves the last saved snapshot available");
        Directory.Move(offline, folder);
        store.Save(library, new(1, DateTimeOffset.UtcNow, scanner.Scan(folder, library.Kind, false)));
        check(store.Load(library)!.Media.Single().Files.Count == 3, "successful explicit refresh replaces the persisted file list");
        check(LibrarySnapshotStore.Key(library) == LibrarySnapshotStore.Key(library with { Name = "renamed" }) && LibrarySnapshotStore.Key(library) != LibrarySnapshotStore.Key(library with { IsRoot = true }), "cache identity ignores display name and separates scan modes");
        var state = new LibraryStateStore { FileMappings = new() { [pending.Path] = new(0, 0, true) } };
        var copy = LibrarySnapshotStore.CopyMedia(loaded); LibrarySnapshotStore.ApplyRecords(copy, state);
        check(copy.Single().Files.Single(f => f.Path == pending.Path).NeedsReview, "legacy automatic 0/0 exclusion does not hide a fractional file needing review");
        state.FileMappings[pending.Path] = new(0, 1, false); LibrarySnapshotStore.ApplyRecords(copy, state);
        var mapped = copy.Single().Files.Single(f => f.Path == pending.Path);
        check(!mapped.NeedsReview && mapped.Season == 0 && mapped.Episode == 1 && mapped.Exclusion.Length == 0 && loaded.Media.Single().Files.Single(f => f.Path == pending.Path).NeedsReview, "manual records overlay a copy without changing physical snapshot numbering");
        copy = LibrarySnapshotStore.CopyMedia(loaded); state.FileMappings[pending.Path] = new(1, 0, true, true); LibrarySnapshotStore.ApplyRecords(copy, state);
        check(!copy.Single().Files.Single(f => f.Path == pending.Path).NeedsReview, "an explicit user exclusion remains excluded even with an unassigned number");
        store.Remove(library); check(store.Load(library) == null, "removing a library removes only its own cache file");
    }
}
