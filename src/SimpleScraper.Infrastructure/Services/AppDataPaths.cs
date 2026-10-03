namespace SimpleScraper.Services;

/// <summary>All persistent state belongs to the current user, independently of the executable location.</summary>
public static class AppDataPaths
{
    public static string Root => Path.GetFullPath(
        Environment.GetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScraper"));

    public static string PathFor(string name)
    {
        if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Use a single data filename.", nameof(name));
        return Path.Combine(Root, name);
    }
}
