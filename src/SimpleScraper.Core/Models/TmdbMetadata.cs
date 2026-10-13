using System.Globalization;
using System.Text.Json.Serialization;

namespace SimpleScraper.Models;

/// <summary>Movie fields consumed by the TMDB adapter and matching policy.</summary>
public sealed class MovieMetadata
{
    [JsonPropertyName("id")] public int TmdbId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("original_title")] public string OriginalTitle { get; set; } = "";
    [JsonPropertyName("release_date")] public string ReleaseDate { get; set; } = "";
    [JsonPropertyName("overview")] public string Overview { get; set; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("vote_average")] public double Rating { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("runtime"), JsonConverter(typeof(OptionalInt32Converter))] public int Runtime { get; set; }
    [JsonPropertyName("genres")] public List<GenreInfo> Genres { get; set; } = [];
    [JsonPropertyName("production_companies")] public List<StudioInfo> ProductionCompanies { get; set; } = [];

    public string OverviewLanguage { get; set; } = "";
    public string OriginalOverview { get; set; } = "";
    [JsonIgnore] public List<CastMember> Cast { get; set; } = [];
    [JsonIgnore] public List<CrewMember> Directors { get; set; } = [];
    [JsonIgnore] public List<CrewMember> Writers { get; set; } = [];
    [JsonIgnore] public List<CrewMember> Producers { get; set; } = [];
    [JsonIgnore] public int Year => TmdbDate.Year(ReleaseDate);
}

/// <summary>Series data; client-calculated runtime and credits are explicit.</summary>
public sealed class TvShowMetadata
{
    [JsonPropertyName("id")] public int TmdbId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("original_name")] public string OriginalName { get; set; } = "";
    [JsonPropertyName("first_air_date")] public string FirstAirDate { get; set; } = "";
    [JsonPropertyName("overview")] public string Overview { get; set; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("vote_average")] public double Rating { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("genres")] public List<GenreInfo> Genres { get; set; } = [];
    [JsonPropertyName("production_companies")] public List<StudioInfo> ProductionCompanies { get; set; } = [];
    [JsonPropertyName("networks")] public List<StudioInfo> Networks { get; set; } = [];
    [JsonPropertyName("seasons")] public List<TvSeasonInfo> Seasons { get; set; } = [];

    public string? ImdbId { get; set; }
    [JsonIgnore] public int EpisodeRunTime { get; set; }
    public string OverviewLanguage { get; set; } = "";
    public string OriginalOverview { get; set; } = "";
    [JsonIgnore] public List<CastMember> Cast { get; set; } = [];
    [JsonIgnore] public List<CrewMember> Crew { get; set; } = [];
    [JsonIgnore] public int Year => TmdbDate.Year(FirstAirDate);
}

public sealed class TvSeasonInfo
{
    [JsonPropertyName("season_number")] public int Number { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("overview")] public string Overview { get; set; } = "";
    [JsonPropertyName("air_date")] public string AirDate { get; set; } = "";
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("vote_average")] public double? Rating { get; set; }
    [JsonPropertyName("episodes")] public List<TvEpisodeMetadata> Episodes { get; set; } = [];
}

public sealed class TvEpisodeMetadata
{
    [JsonPropertyName("id")] public int TmdbId { get; set; }
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("overview")] public string Overview { get; set; } = "";
    [JsonPropertyName("air_date")] public string AirDate { get; set; } = "";
    [JsonPropertyName("still_path")] public string? StillPath { get; set; }
    [JsonPropertyName("runtime"), JsonConverter(typeof(OptionalInt32Converter))] public int Runtime { get; set; }
    [JsonPropertyName("vote_average")] public double Rating { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
}

public sealed class CastMember
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; set; }
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }
}

public sealed class CrewMember
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; set; }
    [JsonPropertyName("department")] public string Department { get; set; } = "";
    [JsonPropertyName("job")] public string Job { get; set; } = "";
}

public sealed class GenreInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class StudioInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal static class TmdbDate
{
    public static int Year(string? date) => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Year : 0;
}
