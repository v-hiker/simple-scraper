namespace SimpleScraper.Models;

public sealed record TranslationSettings
{
    public string Provider { get; init; } = "MyMemory";
    public string MyMemoryEmail { get; init; } = "";
    public string GoogleApiKey { get; init; } = "";
    public string LibreEndpoint { get; init; } = "http://127.0.0.1:5000";
    public string LibreApiKey { get; init; } = "";
}
