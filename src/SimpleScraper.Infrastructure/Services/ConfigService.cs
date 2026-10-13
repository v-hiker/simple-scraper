using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

/// <summary>Small JSON settings store; each operation observes the current on-disk state.</summary>
public sealed class ConfigService
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate;
    public string FilePath { get; }

    public ConfigService(string? path = null)
    {
        FilePath = Path.GetFullPath(path ?? AppDataPaths.PathFor("appsettings.json"));
        _gate = FileLocks.GetOrAdd(FilePath, _ => new());
    }

    public string GetApiKey() => Read(c => TmdbCredentials.Normalize(c.TmdbApiKey));
    public void SetApiKey(string? value) => Change(c => c.TmdbApiKey = TmdbCredentials.Normalize(value));
    public string GetScrapeLanguage() => Read(c => ScrapeLanguage(c.ScrapeLanguage));
    public void SetScrapeLanguage(string? value) => Change(c => c.ScrapeLanguage = ScrapeLanguage(value));
    public string GetUiLanguage() => Read(c => Nonempty(c.UiLanguage, "zh-CN"));
    public void SetUiLanguage(string? value) => Change(c => c.UiLanguage = Nonempty(value, "zh-CN"));
    public string GetUiTheme() => Read(c => Theme(c.UiTheme));
    public void SetUiTheme(string? value) => Change(c => c.UiTheme = Theme(value));
    public bool GetDetailsPaneVisible() => Read(c => c.DetailsPaneVisible);
    public void SetDetailsPaneVisible(bool value) => Change(c => c.DetailsPaneVisible = value);
    public List<string> GetDefaultMetadataSources() => Read(c => Sources(c.DefaultMetadataSources));
    public void SetDefaultMetadataSources(IEnumerable<string>? value) => Change(c => c.DefaultMetadataSources = Sources(value));
    public string GetDefaultOutputProfile() => Read(c => OutputProfile(c.DefaultOutputProfile));
    public void SetDefaultOutputProfile(string? value) => Change(c => c.DefaultOutputProfile = OutputProfile(value));
    public TranslationSettings GetTranslationSettings() => Read(c => (c.Translation ?? new()) with { });
    public void SetTranslationSettings(TranslationSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Change(c => c.Translation = value with { });
    }
    public string GetLastTemplate() => Read(c => c.LastTemplate ?? new AppConfig().LastTemplate);
    public void SetLastTemplate(string value) => Change(c => c.LastTemplate = value ?? "");
    public string GetEpisodeRenameTemplate() => Read(c => c.EpisodeRenameTemplate ?? new AppConfig().EpisodeRenameTemplate);
    public void SetEpisodeRenameTemplate(string value) => Change(c => c.EpisodeRenameTemplate = value ?? "");
    public string GetWorkFolderRenameTemplate() => Read(c => c.WorkFolderRenameTemplate ?? new AppConfig().WorkFolderRenameTemplate);
    public void SetWorkFolderRenameTemplate(string value) => Change(c => c.WorkFolderRenameTemplate = value ?? "");
    public List<string> GetSupportedFormats() => Read(c => (c.SupportedFormats ?? new AppConfig().SupportedFormats).ToList());

    /// <summary>Saves settings-page preferences in one commit, retaining rename templates and other workflow state.</summary>
    public void SavePreferences(AppConfig preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Change(c =>
        {
            c.TmdbApiKey = TmdbCredentials.Normalize(preferences.TmdbApiKey);
            c.UiTheme = Theme(preferences.UiTheme);
            c.UiLanguage = Nonempty(preferences.UiLanguage, "zh-CN");
            c.ScrapeLanguage = ScrapeLanguage(preferences.ScrapeLanguage);
            c.DefaultMetadataSources = Sources(preferences.DefaultMetadataSources);
            c.Translation = (preferences.Translation ?? new()) with { };
            c.DefaultOutputProfile = OutputProfile(preferences.DefaultOutputProfile);
        });
    }

    private T Read<T>(Func<AppConfig, T> select)
    {
        lock (_gate) return select(Load());
    }

    private AppConfig Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions) ?? throw new InvalidDataException("The settings file contains no configuration object.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The settings file is invalid JSON: " + FilePath + ". Restore or remove that file before saving settings.", error);
        }
    }

    private void Change(Action<AppConfig> update)
    {
        lock (_gate)
        {
            var config = Load();
            update(config);
            // Keys are never supplied by the application; whitespace and retired credentials are removed on save.
            config.TmdbApiKey = TmdbCredentials.Normalize(config.TmdbApiKey);
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var staging = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonSerializer.Serialize(config, JsonOptions));
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(staging, FilePath, overwrite: true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }

    private static string Nonempty(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string ScrapeLanguage(string? value) => Nonempty(value, "zh-CN") switch { "zh" => "zh-CN", "en" => "en-US", var language => language };
    private static string OutputProfile(string? value) => value?.Trim().Equals("Emby", StringComparison.OrdinalIgnoreCase) == true ? "Emby" : "Jellyfin";
    private static string Theme(string? value) => value?.Trim().ToLowerInvariant() switch { "light" => "light", "dark" => "dark", _ => "system" };
    private static List<string> Sources(IEnumerable<string>? values)
    {
        if (values is null) return new() { "TMDB" };
        return values.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().Equals("tmdb", StringComparison.OrdinalIgnoreCase) ? "TMDB" : s.Trim().Equals("bangumi", StringComparison.OrdinalIgnoreCase) ? "Bangumi" : "")
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
