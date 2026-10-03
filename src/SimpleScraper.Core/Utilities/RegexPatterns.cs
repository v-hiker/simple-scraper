using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SimpleScraper.Services;

namespace SimpleScraper.Utilities;

/// <summary>Conservative compatibility entry points for filename hints.</summary>
public static class RegexPatterns
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex ReleaseYear = Pattern(@"(?<!\d)(?<year>(?:19|20)\d{2})(?!\d)");
    private static readonly Regex TechnicalTail = Pattern(@"(?:^|[ ._\[(-])(?:\d{3,4}[pi]|[xh][ .]?26[45]|hevc|avc|web[ ._-]?(?:dl|rip)|blu[ ._-]?ray|dvdrip|hdtv)(?=$|[ ._\])\-])");
    private static readonly Regex ShortEpisode = Pattern(@"(?:^|[ ._-])Se[ ._-]*(?<season>\d{1,3})[ .,_-]*Ep[ ._-]*(?<episode>\d{1,3})(?![\d.,&+~])(?=$|[ ._-])");
    private static readonly Regex EpisodeMarker = Pattern(@"(?:S\d{1,3}[ ._-]*EP?\d{1,3}|\d{1,3}x\d{1,3}|(?:Season|Se)[ ._-]*\d{1,3}[ .,_-]*(?:Episode|Ep)[ ._-]*\d{1,3}|(?:Episode|EP|E)[ ._-]*\d{1,3}|第[一二三四五六七八九十\d]+季[ ._-]*第?\d{1,3}[集话話回]|第\d{1,3}[集话話回])");
    private static readonly Regex PrefixGroup = Pattern(@"^\s*\[(?!\d{4}\])[^\]]+\]\s*");
    private static readonly Regex Separator = Pattern(@"[._]+|\s+");
    private static readonly (Regex Pattern, string Name)[] Editions =
    [
        (Pattern(@"\b4k[ ._-]+(?:remaster(?:ed)?|restoration)\b"), "4K Remaster"),
        (Pattern(@"\bdirector(?:['’]?s)?[ ._-]+cut\b"), "Director's Cut"),
        (Pattern(@"\bextended(?:[ ._-]+(?:cut|edition|version))?\b"), "Extended"),
        (Pattern(@"\bimax(?:[ ._-]+enhanced)?\b"), "IMAX"),
        (Pattern(@"\btheatrical(?:[ ._-]+(?:cut|version))?\b"), "Theatrical"),
        (Pattern(@"\bunrated\b"), "Unrated"),
        (Pattern(@"\buncut\b"), "Uncut"),
        (Pattern(@"\bremaster(?:ed)?\b"), "Remastered")
    ];

    public sealed record TvEpisodeParseResult(string ShowName, int Season, int Episode, string EpisodeTitle, double Confidence);
    public sealed record FilenameParseResult(string Title, int Year, double Confidence, string Edition);

    public static TvEpisodeParseResult? ParseTvEpisode(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var name = Stem(filename);
        var episode = MediaRecognition.Parse(filename);
        if (episode is { Extra: true } || episode is { NeedsReview: true }) return null;
        // Resolve the season before the general recognizer's episode-only form.
        var compact = ShortEpisode.Match(name);
        if (compact.Success)
        {
            var season = int.Parse(compact.Groups["season"].Value, CultureInfo.InvariantCulture);
            var number = int.Parse(compact.Groups["episode"].Value, CultureInfo.InvariantCulture);
            return number > 0 ? new(CleanTitle(name[..compact.Index]), season, number, CleanTitle(name[(compact.Index + compact.Length)..]), 0.9) : null;
        }
        if (episode is { Episode: > 0 })
        {
            var marker = EpisodeMarker.Match(name);
            var suffix = marker.Success ? name[(marker.Index + marker.Length)..] : "";
            return new(CleanTitle(episode.Show), episode.Season ?? 1, episode.Episode, CleanTitle(suffix), episode.Explicit ? 0.95 : 0.65);
        }

        return null;
    }

    public static FilenameParseResult ParseFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return new("", 0, 0, "");
        var name = Stem(filename);
        var edition = DetectEdition(name);
        var title = name;
        var year = 0;
        Match? selected = null;
        foreach (Match candidate in ReleaseYear.Matches(name))
        {
            // A leading number can be part of a title such as "2001".
            if (CleanTitle(name[..candidate.Index]).Length == 0) continue;
            if (!AtBoundary(name, candidate.Index - 1) || !AtBoundary(name, candidate.Index + candidate.Length)) continue;
            if (int.Parse(candidate.Groups["year"].Value, CultureInfo.InvariantCulture) > DateTime.UtcNow.Year + 1) continue;
            if (selected == null || Bracketed(name, candidate) || !Bracketed(name, selected)) selected = candidate;
        }
        if (selected != null)
        {
            year = int.Parse(selected.Groups["year"].Value, CultureInfo.InvariantCulture);
            title = name[..selected.Index];
        }
        title = CleanTitle(title);
        var confidence = title.Length == 0 ? 0 : year > 0 ? 0.9 : 0.5;
        return new(title, year, confidence, edition);
    }

    public static string DetectEdition(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        var normalized = filename.Normalize(NormalizationForm.FormKC);
        foreach (var edition in Editions)
            if (edition.Pattern.IsMatch(normalized)) return edition.Name;
        return "";
    }

    private static Regex Pattern(string expression) => new(expression, Options, Timeout);
    private static string Stem(string path) => PrefixGroup.Replace(Path.GetFileNameWithoutExtension(path).Normalize(NormalizationForm.FormKC), "");
    private static bool AtBoundary(string text, int index) => index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);
    private static bool Bracketed(string text, Match match) => match.Index > 0 && match.Index + match.Length < text.Length
        && (text[match.Index - 1], text[match.Index + match.Length]) is ('(', ')') or ('[', ']') or ('{', '}');

    private static string CleanTitle(string text)
    {
        text = PrefixGroup.Replace(text, "");
        var technical = TechnicalTail.Match(text);
        if (technical.Success) text = text[..technical.Index];
        foreach (var edition in Editions) text = edition.Pattern.Replace(text, " ");
        return Separator.Replace(text, " ").Trim(' ', '-', '[', ']', '(', ')', '{', '}');
    }
}
