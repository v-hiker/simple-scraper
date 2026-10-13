using SimpleScraper.Services;

namespace SimpleScraper.Models;

// These are preferred export conventions. Both servers accept much of the common Kodi NFO vocabulary.
public sealed record MediaOutputProfile(string Name, string BackdropName, bool FolderMovieNfo, string TvImdbTag)
{
    public static readonly MediaOutputProfile Jellyfin = new("Jellyfin", "fanart.jpg", true, "imdb_id");
    public static readonly MediaOutputProfile Emby = new("Emby", "backdrop.jpg", false, "imdbid");
    public static MediaOutputProfile ForLibrary(MediaLibrary? library, string legacyProfile = "Jellyfin") => string.Equals(library?.OutputProfile ?? legacyProfile, "Emby", StringComparison.OrdinalIgnoreCase) ? Emby : Jellyfin;
    public string SpecialsFolder => Name == "Emby" ? "Specials" : "Season 00";
    public string ExtrasFolder => "extras";
    public string SeasonFolder(int season) => season == 0 ? SpecialsFolder : $"Season {season:00}";
    public string EpisodeFolder(LibraryMedia media, LocalMediaFile file, bool organizeSpecials = true)
    {
        var current = Path.GetDirectoryName(file.Path)!;
        if (file.Season == 0 && !organizeSpecials) return current;
        // Keep a valid existing regular-season directory, including Season 3 / 第三季.
        // The selected show's root is never itself treated as its season directory.
        if (file.Season > 0 && !string.Equals(current, media.Folder, StringComparison.OrdinalIgnoreCase) && MediaRecognition.SeasonFolder(current) == file.Season) return current;
        return Path.Combine(Path.GetFullPath(media.Folder), SeasonFolder(file.Season));
    }
    public string Describe() => $"{Name} · {(FolderMovieNfo ? "movie.nfo" : L.Text("Video basename") + ".nfo")} / tvshow.nfo / season.nfo · poster.jpg / {BackdropName} · S01E01";
}
