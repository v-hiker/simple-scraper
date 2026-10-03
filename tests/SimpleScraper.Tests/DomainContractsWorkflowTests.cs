using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Utilities;

static class DomainContractsWorkflowTests
{
    public static void Run(Action<bool, string> check)
    {
        var numericTitle = RegexPatterns.ParseFilename("2001.A.Space.Odyssey.1968.2160p.mkv");
        check(numericTitle.Title == "2001 A Space Odyssey" && numericTitle.Year == 1968, "numeric movie title is preserved while trailing release year is extracted");
        var standalone = RegexPatterns.ParseFilename("1917.mkv");
        check(standalone.Title == "1917" && standalone.Year == 0, "a title consisting of a year-like number remains a title");
        var futureNumber = DateTime.UtcNow.Year + 20;
        var numberedTitle = RegexPatterns.ParseFilename($"Movie.Number.{futureNumber}.mkv");
        check(numberedTitle.Title == $"Movie Number {futureNumber}" && numberedTitle.Year == 0, "a distant future number in a movie title is not mistaken for an available release year");
        var chinese = RegexPatterns.ParseFilename("测试电影 (2026) 1080p.mkv");
        check(chinese.Title == "测试电影" && chinese.Year == 2026, "Chinese movie title and bracketed release year remain intact");
        var edition = RegexPatterns.ParseFilename("Movie.Director's.Cut.1080p.mkv");
        check(edition.Title == "Movie" && edition.Edition == "Director's Cut", "edition is retained separately from the movie search title");
        check(RegexPatterns.DetectEdition("Movie.4K.Remastered.mkv") == "4K Remaster", "specific restoration edition takes precedence over generic remaster");
        var episode = RegexPatterns.ParseTvEpisode("Show.S03E07.Pilot.1080p.mkv");
        check(episode is { ShowName: "Show", Season: 3, Episode: 7, EpisodeTitle: "Pilot" }, "episode compatibility parser keeps show, season, number and episode title");
        var compact = RegexPatterns.ParseTvEpisode("Show Se03 Ep07 - Pilot.mkv");
        check(compact is { Season: 3, Episode: 7, EpisodeTitle: "Pilot" }, "compact season marker is not lost to an episode-only fallback");
        check(RegexPatterns.ParseTvEpisode("Show S01E11.5.mkv") == null && RegexPatterns.ParseTvEpisode("Show S01E01E02.mkv") == null, "fractional and multiple episodes never become a single compatibility match");
        check(RegexPatterns.ParseTvEpisode("Show NCOP01.mkv") == null, "opening credit video is not an episode compatibility match");
        check(FileFormatValidator.IsVideoFile("VIDEO.MKV", out var extension) && extension == ".MKV" && FileFormatValidator.IsVideoFile("show.ts") && FileFormatValidator.IsVideoFile("legacy.flv"), "format inventory consistently recognizes upper-case, transport-stream and legacy video names");
        check(!FileFormatValidator.IsVideoFile("poster.jpg") && !FileFormatValidator.IsVideoFile(null), "nonvideo and absent paths do not become playable media");
        check(WindowsFileName.Sanitize("  Movie:<>\u0001 . ") == "Movie", "Windows filename cleanup removes forbidden characters and trailing periods");
        var movie = JsonSerializer.Deserialize<MovieMetadata>("{\"id\":12,\"title\":\"Film\",\"release_date\":\"2020-03-01\",\"runtime\":null}")!;
        check(movie.TmdbId == 12 && movie.Title == "Film" && movie.Year == 2020 && movie.Runtime == 0, "TMDB movie contract accepts absent duration without losing identity or release year");
        var season = JsonSerializer.Deserialize<TvSeasonInfo>("{\"season_number\":0,\"episodes\":[{\"id\":3,\"season_number\":0,\"episode_number\":1,\"runtime\":24}]}")!;
        check(season.Number == 0 && season.Episodes.Single() is { TmdbId: 3, SeasonNumber: 0, EpisodeNumber: 1, Runtime: 24 }, "special season and nested episode JSON map to their explicit contract");
        check(new AppConfig() is { UiLanguage: "zh-CN", ScrapeLanguage: "zh-CN", TmdbApiKey: "" } defaults && defaults.DefaultMetadataSources.SequenceEqual(new[] { "TMDB" }), "new installation uses Chinese preferences and requires a personal TMDB key");
    }
}
