using System.Text;
using System.Text.RegularExpressions;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public static class ExtraMedia
{
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    { "extras", "other", "featurettes", "shorts", "scenes", "clips", "behind the scenes", "deleted scenes", "interviews", "trailers", "OPED", "OP", "ED", "Menu", "Menus" };
    private static readonly Regex Tag = new(@"(?:^|[\W_])(?<tag>NCOP|NCED|OP|ED|Menu|PV|CM)(?<n>\d{1,3})?(?<v>v\d+)?(?=$|[\W_])", RegexOptions.IgnoreCase);
    public static bool IsExtraFolder(string folder) => Folders.Contains(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    public static MediaExtraInfo? Identify(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return null;
        var stem = Path.GetFileNameWithoutExtension(path).Normalize(NormalizationForm.FormKC);
        var match = Tag.Match(stem);
        if (match.Success)
        {
            var tag = match.Groups["tag"].Value.ToUpperInvariant(); if (tag == "MENU") tag = "Menu";
            var number = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value).ToString("00") : "";
            return new(tag + number + match.Groups["v"].Value.ToLowerInvariant());
        }
        if (Regex.IsMatch(stem, @"-(?:behindthescenes|deleted|deletedscene|featurette|interview|scene|short|trailer|other|extra)$", RegexOptions.IgnoreCase)) return new("");
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(Folders.Contains) ? new("") : null;
    }
    public static int SeasonHint(string root, string path)
    {
        for (var folder = Path.GetDirectoryName(path); folder != null; folder = Path.GetDirectoryName(folder))
        {
            if (MediaRecognition.SeasonFolder(folder) is int season) return season;
            if (folder.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
        }
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(?:^|[\W_])S(?<s>\d{1,3})(?=E|[\W_]|$)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups["s"].Value) : 0;
    }
    public static void ApplyHints(LibraryMedia media)
    {
        foreach (var file in media.Files)
        {
            file.Extra = Identify(media.Folder, file.Path);
            if (file.Extra == null || file.ManuallyExcluded) continue;
            file.Season = SeasonHint(media.Folder, file.Path); file.Exclusion = L.Text("Extra video"); file.NeedsReview = false;
        }
    }
    public static string DestinationFolder(string root, string source, MediaOutputProfile profile)
    {
        if (!Path.GetFullPath(source).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.Text("Extra videos must remain inside the media folder."));
        var directory = Path.GetDirectoryName(source)!;
        // Season extras stay with their season; unassigned OPED folders move to
        // series-level extras. Only documented generic extras folders are used.
        for (var current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            if (current.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
            if (MediaRecognition.SeasonFolder(current) is > 0) return Path.Combine(current, profile.ExtrasFolder);
        }
        return Path.Combine(Path.GetFullPath(root), profile.ExtrasFolder);
    }
    public static string Filename(LibraryMedia media, LocalMediaFile file, MediaExtraInfo info)
    {
        if (info.Tag.Length == 0) return Path.GetFileNameWithoutExtension(file.Path);
        var title = media.Metadata?.Title ?? media.Title;
        var season = SeasonHint(media.Folder, file.Path);
        return Utilities.WindowsFileName.Sanitize(title + (season > 0 ? $" - S{season:00}" : "") + " - " + info.Tag);
    }
}
