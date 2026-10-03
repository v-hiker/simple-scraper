using System.Text;
using System.Text.RegularExpressions;

namespace SimpleScraper.Services;

public sealed record EpisodeName(int? Season, int Episode, string Show, bool Explicit, bool Extra = false, string SourceNumber = "")
{
    public bool NeedsReview => SourceNumber.Length > 0;
}

// Filename hints do not identify a metadata title. Ambiguous numbers require
// corroborating files or a season directory before automatic classification.
public static class MediaRecognition
{
    private const string Number = @"(?<e>\d{1,3})(?<extra>[.,]\d+(?![\dA-Za-z])|(?:\s*(?:-|&|\+|~|E)\s*\d{1,3}(?![\dA-Za-z]))*)(?!\d)";
    private static readonly Regex[] ExplicitPatterns =
    {
        new(@"(?:^|[\W_])S(?<s>\d{1,3})[ ._-]*EP?" + Number, RegexOptions.IgnoreCase),
        new(@"(?:^|[\W_])(?<s>\d{1,3})x" + Number, RegexOptions.IgnoreCase),
        new(@"Season[ ._-]*(?<s>\d{1,3})[ .,_-]*Episode[ ._-]*" + Number, RegexOptions.IgnoreCase),
        new(@"第(?<s>[一二三四五六七八九十\d]+)季[ ._-]*第?" + Number + @"[集话話回]"),
        new(@"第" + Number + @"[集话話回]"),
        new(@"(?:^|[\W_])(?:Episode|EP|E)[ ._-]*" + Number + @"(?=$|[\W_])", RegexOptions.IgnoreCase),
    };
    private static readonly Regex Brackets = new(@"\[" + Number + @"(?:v\d+)?\]", RegexOptions.IgnoreCase);
    private static readonly Regex Dash = new(@"\s+-\s*" + Number + @"(?=$|[ ._\[(-])");
    private static readonly Regex Bare = new(@"^\[?" + Number + @"\]?(?=$|[ ._-])");
    private static readonly Regex Extras = new(@"(?:^|[\W_])(?:NCOP|NCED|OP|ED|PV|CM|Menu)(?:\d{1,3})?(?=$|[\W_])", RegexOptions.IgnoreCase);
    private static readonly Regex Specials = new(@"(?:^|[\W_])(?:SP|OVA|OAD)[ ._-]*(?<e>\d{1,3})(?![\d.,])(?=$|[\W_])", RegexOptions.IgnoreCase);

    public static int? SeasonFolder(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Normalize(NormalizationForm.FormKC);
        if (Regex.IsMatch(name, @"^(Specials?|SP|OVA|OAD|特别篇|特別篇)$", RegexOptions.IgnoreCase)) return 0;
        var match = Regex.Match(name, @"^(?:Season[ ._-]*|S)(?<n>\d{1,3})$", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(name, @"^第?(?<n>\d{1,3}|[一二三四五六七八九十]{1,3})季$");
        return match.Success ? ChineseNumber(match.Groups["n"].Value) : null;
    }

    private static int? ChineseNumber(string number)
    {
        if (int.TryParse(number, out var n)) return n;
        const string digits = "一二三四五六七八九";
        var tens = number.Split('十');
        if (tens.Length == 1) return number.Length == 1 ? digits.IndexOf(number[0]) + 1 : null;
        if (tens.Length != 2 || tens.Any(p => p.Length > 1)) return null;
        return (tens[0].Length == 0 ? 1 : digits.IndexOf(tens[0][0]) + 1) * 10 + (tens[1].Length == 0 ? 0 : digits.IndexOf(tens[1][0]) + 1);
    }

    public static EpisodeName? Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Normalize(NormalizationForm.FormKC);
        if (Extras.IsMatch(name)) return new(null, 0, "", true, true);
        // A release group is not a show title or episode number.
        name = Regex.Replace(name, @"^\[(?!\d)[^\]]+\]\s*", "");
        if (Extras.IsMatch(name)) return new(null, 0, "", true, true);
        var special = Specials.Match(name);
        if (special.Success) return new(0, int.Parse(special.Groups["e"].Value), name[..special.Index].Trim(), true);
        foreach (var pattern in ExplicitPatterns)
        {
            var match = pattern.Match(name);
            if (match.Success) return Parsed(match, name, true);
        }
        var dotted = Regex.Match(name, @"(?:^|[ ._-])(?<s>\d{1,2})\.(?<e>\d{2,3})(?=$|[ ._-])");
        if (dotted.Success) return Parsed(dotted, name, false);
        foreach (var pattern in new[] { Brackets, Dash, Bare })
        {
            var match = pattern.Match(name);
            if (match.Success) return Parsed(match, name, false);
        }
        return null;
    }

    private static EpisodeName Parsed(Match match, string name, bool explicitNumber)
    {
        var original = match.Groups["e"].Value + match.Groups["extra"].Value;
        return new(match.Groups["s"].Success ? ChineseNumber(match.Groups["s"].Value) : null,
            match.Groups["extra"].Length > 0 ? 0 : int.Parse(match.Groups["e"].Value),
            name[..match.Index].Trim(' ', '.', '_', '-'), explicitNumber, false,
            match.Groups["extra"].Length > 0 ? original.Replace(',', '.') : "");
    }

    public static bool HasEpisodeNames(IEnumerable<string> paths)
    {
        var names = paths.Where(Utilities.FileFormatValidator.IsVideoFile).Select(Parse).OfType<EpisodeName>().Where(p => !p.Extra).ToList();
        if (names.Any(p => p.Explicit)) return true;
        return names.Where(p => p.Show.Length > 0).GroupBy(p => p.Show, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Select(p => p.NeedsReview ? p.SourceNumber : p.Episode.ToString()).Distinct().Count() >= 2);
    }
}
