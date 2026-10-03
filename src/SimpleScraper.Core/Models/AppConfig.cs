using SimpleScraper.Utilities;

namespace SimpleScraper.Models;

/// <summary>Persisted user preferences for the library workflow.</summary>
public sealed class AppConfig
{
    public string UiLanguage { get; set; } = "zh-CN";
    public string UiTheme { get; set; } = "system";
    public string ScrapeLanguage { get; set; } = "zh-CN";
    public string? TmdbApiKey { get; set; } = "";
    public bool DetailsPaneVisible { get; set; } = true;
    public string DefaultOutputProfile { get; set; } = "Jellyfin";
    public List<string> DefaultMetadataSources { get; set; } = ["TMDB"];
    public TranslationSettings Translation { get; set; } = new();
    public string LastTemplate { get; set; } = "{Title} ({Year})";
    public string EpisodeRenameTemplate { get; set; } = "{Title} - S{Season}E{Episode} - {EpisodeTitle}";
    public string WorkFolderRenameTemplate { get; set; } = "{Title} ({Year})";
    public List<string> SupportedFormats { get; set; } = FileFormatValidator.GetSupportedFormats().ToList();
}
