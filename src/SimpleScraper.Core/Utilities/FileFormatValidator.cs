namespace SimpleScraper.Utilities;

/// <summary>One format inventory for scanning, cached recognition and settings.</summary>
public static class FileFormatValidator
{
    private static readonly string[] Extensions = [".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".ts", ".webm", ".wmv"];
    private static readonly IReadOnlyList<string> Formats = Array.AsReadOnly(Extensions);

    public static bool IsVideoFile(string? path) => IsVideoFile(path, out _);

    public static bool IsVideoFile(string? path, out string? extension)
    {
        extension = string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path);
        return extension.Length > 0 && Formats.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetSupportedFormats() => Formats;
}
