namespace SimpleScraper.Services;

/// <summary>All persistent state belongs to the current user, independently of the executable location.</summary>
public static class AppDataPaths
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScraper");
    public static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME");
            if (string.IsNullOrWhiteSpace(configured)) return DefaultRoot;
            if (!Path.IsPathFullyQualified(configured))
                throw new ArgumentException("SIMPLE_SCRAPER_DATA_HOME must be an absolute directory path.");
            return Path.GetFullPath(configured);
        }
    }

    public static string PathFor(string name)
    {
        if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Use a single data filename.", nameof(name));
        return Path.Combine(Root, name);
    }
}
