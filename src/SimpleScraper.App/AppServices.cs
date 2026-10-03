using SimpleScraper.Services;

namespace SimpleScraper;

/// <summary>The desktop composition root. Pages receive services instead of choosing storage and network implementations.</summary>
public sealed class AppServices
{
    private static readonly object PerformanceLogLock = new();
    public required ConfigService Config { get; init; }
    public required LibraryScanner Scanner { get; init; }
    public required LibrarySnapshotStore Snapshots { get; init; }
    public required LibraryRenameService Rename { get; init; }
    public Uri? RepositoryUri { get; init; }
    public Func<LibraryStateStore> LoadState { get; init; } = LibraryStateStore.Load;
    public Func<TmdbApiClient> Tmdb { get; init; } = null!;
    public Func<BangumiMetadataProvider> Bangumi { get; init; } = () => new();
    public Func<ImageDownloadService> Images { get; init; } = () => new();
    public Func<ActorImageCache> ActorImages { get; init; } = () => new();
    public Func<LibraryOutputService> Output { get; init; } = null!;
    public Func<string, CancellationToken, IMetadataProvider> MetadataProvider { get; init; } = null!;

    public static AppServices CreateDefault()
    {
        var config = new ConfigService();
        return new()
        {
            Config = config,
            Scanner = new LibraryScanner(),
            Snapshots = new LibrarySnapshotStore(),
            Rename = new LibraryRenameService(),
            RepositoryUri = RepositoryFromAssembly(),
            Tmdb = () => new TmdbApiClient(config.GetApiKey(), config.GetScrapeLanguage()),
            Output = () => new LibraryOutputService(new ImageDownloadService(), new TmdbApiClient(config.GetApiKey(), config.GetScrapeLanguage()), new BangumiMetadataProvider()),
            MetadataProvider = (source, token) => source switch
            {
                "TMDB" => new TmdbMetadataProvider(new TmdbApiClient(config.GetApiKey(), config.GetScrapeLanguage(), cancellationToken: token)),
                "Bangumi" => new BangumiMetadataProvider(cancellationToken: token),
                _ => throw new ArgumentException(source, nameof(source)),
            },
        };
    }

    private static Uri? RepositoryFromAssembly()
    {
        var value = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
    }

    public void WriteDiagnostic(Exception exception)
    {
        try
        {
            var directory = Path.Combine(AppDataPaths.Root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "desktop.log"), $"{DateTimeOffset.UtcNow:O}\n{exception}\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(failure); }
    }

    /// <summary>Local phase timings contain counts and generated operation IDs, never library paths or provider credentials.</summary>
    public void WritePerformance(string operation, string phase, double elapsedMs, int mediaCount, int? rowCount = null, bool refresh = false, string? outcome = null)
    {
        try
        {
            var directory = Path.Combine(AppDataPaths.Root, "logs");
            var record = System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, operation, phase, elapsedMs = Math.Round(elapsedMs, 2), mediaCount, rowCount, refresh, outcome });
            lock (PerformanceLogLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "library-performance.jsonl"), record + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(failure); }
    }
}
