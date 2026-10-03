namespace SimpleScraper.Utilities;

public static class WindowsFileName
{
    /// <summary>Remove characters forbidden in a Windows filename, preserving title spacing.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string forbidden = "<>:\"/\\|?*";
        var cleaned = new string(name.Select(c => c < ' ' || forbidden.Contains(c) ? ' ' : c).ToArray());
        return cleaned.Trim().TrimEnd('.').Trim();
    }
}
