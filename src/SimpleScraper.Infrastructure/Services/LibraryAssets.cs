using System.Text;
using System.Xml.Linq;

namespace SimpleScraper.Services;

public static class LibraryAssets
{
    public static string? FindVideo(string folder) => Directory.EnumerateFiles(folder)
        .FirstOrDefault(Utilities.FileFormatValidator.IsVideoFile);
    public static string? FindNfo(string folder, bool tv = false)
    {
        if (tv) return Existing(Path.Combine(folder, "tvshow.nfo"));
        var video = FindVideo(folder);
        return (video == null ? null : Existing(Path.ChangeExtension(video, ".nfo")))
            ?? Existing(Path.Combine(folder, "movie.nfo"))
            ?? Directory.EnumerateFiles(folder, "*.nfo").FirstOrDefault();
    }
    public static string? FindArtwork(string folder, string aspect, string? video = null)
    {
        if (aspect == "clearlogo") return FindArtwork(folder, "logo", video, "clearlogo");
        return FindArtwork(folder, aspect, video, aspect);
    }
    private static string? FindArtwork(string folder, string aspect, string? video, string preferred)
    {
        if (preferred != aspect)
        {
            var match = FindArtwork(folder, preferred, video, preferred);
            if (match != null) return match;
        }
        foreach (var extension in new[]{".jpg", ".png", ".jpeg", ".webp"})
        {
            var named = Existing(Path.Combine(folder, aspect + extension));
            if(named != null) return named;
            if(video != null)
            {
                named = Existing(Path.Combine(folder, Path.GetFileNameWithoutExtension(video) + "-" + aspect + extension));
                if(named != null) return named;
            }
            named = Directory.EnumerateFiles(folder, "*-" + aspect + extension).FirstOrDefault(p => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^season(?:\d+|-specials)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if(named != null) return named;
        }
        return null;
    }
    public static string? FindNamedArtwork(string folder, string name) => new[] { ".jpg", ".png", ".jpeg", ".webp" }.Select(ext => Existing(Path.Combine(folder, name + ext))).FirstOrDefault(p => p != null);
    /// <summary>Scan-only lookup: the snapshot already includes candidate files.</summary>
    public static string? FindArtwork(LibraryDirectoryInventory inventory, string aspect, string? video = null)
    {
        if (aspect == "clearlogo") return FindArtworkInDirectory(inventory, "clearlogo", video)
            ?? FindArtworkInDirectory(inventory, "logo", video);
        return FindArtworkInDirectory(inventory, aspect, video);
    }
    private static string? FindArtworkInDirectory(LibraryDirectoryInventory inventory, string aspect, string? video)
    {
        foreach (var extension in new[] { ".jpg", ".png", ".jpeg", ".webp" })
        {
            var named = inventory.ExistingFile(aspect + extension);
            if (named != null) return named;
            if (video != null)
            {
                named = inventory.ExistingFile(Path.GetFileNameWithoutExtension(video) + "-" + aspect + extension);
                if (named != null) return named;
            }
            var suffix = "-" + aspect + extension;
            named = inventory.Files.FirstOrDefault(p => Path.GetFileName(p).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^season(?:\d+|-specials)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (named != null) return named;
        }
        return null;
    }
    public static string? FindNamedArtwork(LibraryDirectoryInventory inventory, string name) => new[] { ".jpg", ".png", ".jpeg", ".webp" }
        .Select(ext => inventory.ExistingFile(name + ext)).FirstOrDefault(p => p != null);
    private static string? Existing(string file) => File.Exists(file) ? file : null;
    public static bool HasActorPhotos(string folder, bool tv)
    {
        var nfo = FindNfo(folder, tv);
        if (nfo != null)
        {
            try
            {
                var actors = XDocument.Load(nfo).Root?.Elements("actor").ToList();
                if (actors != null)
                    return actors.All(actor =>
                    {
                        var thumb = actor.Element("thumb")?.Value;
                        return string.IsNullOrWhiteSpace(thumb) || Uri.TryCreate(thumb, UriKind.Absolute, out var uri)
                            && uri.Scheme is "https" or "http"
                            || File.Exists(Path.IsPathRooted(thumb) ? thumb : Path.Combine(folder, thumb));
                    });
            }
            catch (System.Xml.XmlException) { }
        }
        var actorsFolder = Path.Combine(folder, ".actors");
        return Directory.Exists(actorsFolder) && Directory.EnumerateFiles(actorsFolder, "*.jpg").Any();
    }
    public static bool HasEpisodeGaps(string folder)
    {
        var videos = Directory.EnumerateFiles(folder)
            .Concat(Directory.EnumerateDirectories(folder).SelectMany(Directory.EnumerateFiles))
            .Where(Utilities.FileFormatValidator.IsVideoFile)
            .Where(file => Utilities.RegexPatterns.ParseTvEpisode(Path.GetFileName(file)) != null);
        foreach (var video in videos)
        {
            var nfo = Path.ChangeExtension(video, ".nfo");
            if (!File.Exists(nfo)) return true;
            try
            {
                var thumb = XDocument.Load(nfo).Root?.Element("thumb")?.Value;
                if (!string.IsNullOrWhiteSpace(thumb) && !Uri.TryCreate(thumb, UriKind.Absolute, out _)
                    && !File.Exists(Path.Combine(Path.GetDirectoryName(video)!, thumb))) return true;
            }
            catch (System.Xml.XmlException) { return true; }
        }
        return false;
    }
    public static int? ReadTmdbId(string folder, bool tv = false)
    {
        var nfo = FindNfo(folder, tv);
        if(nfo == null) return null;
        try
        {
            var root = XDocument.Load(nfo).Root;
            if(root?.Name.LocalName != (tv ? "tvshow" : "movie")) return null;
            var id = root.Elements("uniqueid").FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("type"), "tmdb", StringComparison.OrdinalIgnoreCase))?.Value
                ?? root.Element("tmdbid")?.Value;
            return int.TryParse(id, out var value) && value > 0 ? value : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }
    /// <summary>CreateNew handles concurrent writers too; an existing file is never truncated.</summary>
    public static bool WriteNewText(string path, string content)
    {
        if (File.Exists(path)) return false;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            try { File.Move(temporaryPath, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
