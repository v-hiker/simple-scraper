namespace SimpleScraper.Models;

public sealed record LibraryColumn(string Id, string Label, int DefaultWidth, bool DefaultVisible = false);
public sealed class LibraryColumnPreference
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; }
    public int Width { get; set; }
    public override string ToString() => L.Text(LibraryColumns.All.FirstOrDefault(c => c.Id == Id)?.Label ?? Id);
}
public static class LibraryColumns
{
    public static readonly IReadOnlyList<LibraryColumn> All = new[]
    {
        new LibraryColumn("title", "Title", 250, true), new("year", "Year", 64, true), new("type", "Type", 72, true),
        new("match", "Match status", 100, true), new("nfo", "NFO", 72, true), new("poster", "Poster", 72, true),
        new("original", "Original title", 220), new("rating", "Rating", 68, true), new("source", "Metadata source", 120),
        new("seasons", "Seasons", 64, true), new("episodes", "Episodes", 72, true), new("excluded", "Excluded files", 84),
        new("ids", "Provider IDs", 180), new("path", "Folder path", 300)
        ,new("filename", "Filename", 280)
    };
    public static List<LibraryColumnPreference> Normalize(IEnumerable<LibraryColumnPreference>? saved)
    {
        var result = new List<LibraryColumnPreference>();
        foreach (var setting in saved ?? Array.Empty<LibraryColumnPreference>())
        {
            var column = All.FirstOrDefault(c => c.Id == setting.Id);
            if (column != null && result.All(c => c.Id != column.Id)) result.Add(new() { Id = column.Id, Visible = column.Id == "title" || setting.Visible, Width = Math.Clamp(setting.Width, 50, 600) });
        }
        foreach (var column in All.Where(c => result.All(p => p.Id != c.Id))) result.Add(new() { Id = column.Id, Visible = column.DefaultVisible, Width = column.DefaultWidth });
        return result;
    }
    // A header drag changes visible slots; hidden preferences retain their positions.
    public static bool MoveVisible(IList<LibraryColumnPreference> columns, int source, int boundary)
    {
        var slots = Enumerable.Range(0, columns.Count).Where(i => columns[i].Visible).ToList();
        if (source < 0 || source >= slots.Count || boundary < 0 || boundary > slots.Count) return false;
        var destination = boundary > source ? boundary - 1 : boundary;
        if (destination == source) return false;
        var visible = slots.Select(i => columns[i]).ToList(); var moving = visible[source]; visible.RemoveAt(source); visible.Insert(destination, moving);
        for (var i = 0; i < slots.Count; i++) columns[slots[i]] = visible[i];
        return true;
    }
}
public enum RenamePreviewState { Changed, Unchanged, Excluded, Unmatched, Conflict }
public sealed class RenamePreviewGroup
{
    public LocalMediaFile File { get; init; } = new();
    public string ProposedName { get; init; } = "";
    public string Detail { get; set; } = "";
    public RenamePreviewState State { get; set; }
    public bool Selected { get; set; }
    public bool IsExtra { get; init; }
    public List<FileRename> Operations { get; init; } = new();
    public List<string> RelatedFiles { get; init; } = new();
}
