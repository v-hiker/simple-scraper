using System.Globalization;
using System.Net;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

/// <summary>TMDB v3 transport and response mapping. An injected HttpClient remains owned by its caller.</summary>
public sealed class TmdbApiClient
{
    private static readonly HttpClient DefaultHttp = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string _credential;
    private readonly string _language;
    private readonly Uri _apiRoot;
    private readonly int _attemptLimit;

    public CancellationToken CancellationToken { get; }

    public TmdbApiClient(string? apiKey, string language = "zh-CN", HttpClient? http = null,
        CancellationToken cancellationToken = default, Uri? apiRoot = null, int maxAttempts = 3)
    {
        if (maxAttempts < 1 || maxAttempts > 5) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _credential = TmdbCredentials.Normalize(apiKey);
        _language = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim();
        _http = http ?? DefaultHttp;
        _apiRoot = apiRoot ?? new Uri("https://api.themoviedb.org/3/");
        if (!_apiRoot.IsAbsoluteUri || (_apiRoot.Scheme != Uri.UriSchemeHttps && _apiRoot.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("The TMDB API root must be an HTTP or HTTPS URI.", nameof(apiRoot));
        _attemptLimit = maxAttempts;
        CancellationToken = cancellationToken;
    }

    public async Task<List<MovieMetadata>> SearchMovieAsync(string query, int? year = null)
    {
        var fields = new List<(string, string)> { ("query", SearchQuery(query)) };
        if (year is >= 1000 and <= 9999) fields.Add(("primary_release_year", year.Value.ToString(CultureInfo.InvariantCulture)));
        using var json = await ReadAsync("search/movie", true, fields.ToArray());
        return ReadList<MovieMetadata>(json?.RootElement ?? default, "results");
    }

    public async Task<List<TvShowMetadata>> SearchTvAsync(string query, int? year = null)
    {
        var fields = new List<(string, string)> { ("query", SearchQuery(query)) };
        if (year is >= 1000 and <= 9999) fields.Add(("first_air_date_year", year.Value.ToString(CultureInfo.InvariantCulture)));
        using var json = await ReadAsync("search/tv", true, fields.ToArray());
        return ReadList<TvShowMetadata>(json?.RootElement ?? default, "results");
    }

    public async Task<MovieMetadata?> GetMovieDetailsAsync(int id)
    {
        using var json = await DetailAsync(id, false);
        if (json is null) return null;
        var root = json.RootElement;
        var movie = Deserialize<MovieMetadata>(root);
        movie.OverviewLanguage = _language;
        movie.OriginalOverview = OverviewText.TmdbOriginal(root, _language);
        if (string.IsNullOrWhiteSpace(movie.Overview))
        {
            var fallback = await FallbackPlotAsync(id, false);
            if (fallback.Length > 0) { movie.Overview = fallback; movie.OverviewLanguage = "en-US"; }
        }
        movie.Cast = CreditList<CastMember>(root, "cast");
        var crew = CreditList<CrewMember>(root, "crew");
        movie.Directors = crew.Where(p => p.Job.Equals("Director", StringComparison.OrdinalIgnoreCase)).ToList();
        movie.Writers = crew.Where(p => p.Department.Equals("Writing", StringComparison.OrdinalIgnoreCase) || p.Job is "Writer" or "Screenplay" or "Story").ToList();
        movie.Producers = crew.Where(p => p.Job.Contains("Producer", StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.IsNullOrEmpty(movie.ImdbId)) movie.ImdbId = NestedText(root, "external_ids", "imdb_id");
        return movie;
    }

    public async Task<TvShowMetadata?> GetTvDetailsAsync(int id)
    {
        using var json = await DetailAsync(id, true);
        if (json is null) return null;
        var root = json.RootElement;
        var show = Deserialize<TvShowMetadata>(root);
        show.OverviewLanguage = _language;
        show.OriginalOverview = OverviewText.TmdbOriginal(root, _language);
        if (string.IsNullOrWhiteSpace(show.Overview))
        {
            var fallback = await FallbackPlotAsync(id, true);
            if (fallback.Length > 0) { show.Overview = fallback; show.OverviewLanguage = "en-US"; }
        }
        show.Cast = CreditList<CastMember>(root, "cast");
        show.Crew = CreditList<CrewMember>(root, "crew");
        show.ImdbId = NestedText(root, "external_ids", "imdb_id");
        if (root.TryGetProperty("episode_run_time", out var durations) && durations.ValueKind == JsonValueKind.Array)
            show.EpisodeRunTime = durations.EnumerateArray().Select(ReadDuration).FirstOrDefault(n => n > 0);
        return show;
    }

    public async Task<List<CastMember>> GetMovieCastAsync(int id)
    {
        ValidateId(id);
        using var json = await ReadAsync($"movie/{id}/credits", true);
        return ReadList<CastMember>(json?.RootElement ?? default, "cast");
    }

    public async Task<TvSeasonInfo> GetTvSeasonDetailsAsync(int id, int season)
    {
        ValidateId(id);
        if (season < 0) throw new ArgumentOutOfRangeException(nameof(season));
        using var json = await ReadAsync($"tv/{id}/season/{season}", true);
        if (json is null) throw new InvalidDataException(L.Text("Metadata not found."));
        var result = Deserialize<TvSeasonInfo>(json.RootElement);
        result.Number = season;
        return result;
    }

    public async Task<string> GetOriginalOverviewAsync(int id, bool isTv)
    {
        using var json = await DetailAsync(id, isTv);
        return json is null ? "" : OverviewText.TmdbOriginal(json.RootElement, _language);
    }

    public async Task<string> GetClearLogoAsync(int id, bool isTv)
    {
        ValidateId(id);
        using var json = await ReadAsync($"{(isTv ? "tv" : "movie")}/{id}/images", false);
        if (json is null || !json.RootElement.TryGetProperty("logos", out var logos) || logos.ValueKind != JsonValueKind.Array) return "";
        var preferred = _language.Split('-')[0];
        var candidates = logos.EnumerateArray()
            .Where(e => Text(e, "file_path").Length > 0)
            .OrderBy(e => LogoPriority(Text(e, "iso_639_1"), preferred))
            .ThenByDescending(e => Number(e, "vote_average"))
            .ThenByDescending(e => Number(e, "vote_count"))
            .ToList();
        if (candidates.Count == 0) return "";
        var path = Text(candidates[0], "file_path");
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) path = path[..^4] + ".png";
        return GetImageUrl(path);
    }

    public string GetImageUrl(string? path, string size = "original")
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Uri.TryCreate(path, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") return url.AbsoluteUri;
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#')) return "";
        if (size != "original" && !(size.StartsWith('w') && int.TryParse(size.AsSpan(1), out var width) && width > 0))
            throw new ArgumentException("Use original or a positive TMDB image width.", nameof(size));
        return "https://image.tmdb.org/t/p/" + size + path;
    }

    private Task<JsonDocument?> DetailAsync(int id, bool tv)
    {
        ValidateId(id);
        return ReadAsync($"{(tv ? "tv" : "movie")}/{id}", true, ("append_to_response", "credits,external_ids,translations"));
    }

    private async Task<string> FallbackPlotAsync(int id, bool tv)
    {
        if (_language.Split('-')[0].Equals("en", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            using var json = await ReadAsync($"{(tv ? "tv" : "movie")}/{id}", false, ("language", "en-US"));
            return json is null ? "" : Text(json.RootElement, "overview");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException) { return ""; }
    }

    private async Task<JsonDocument?> ReadAsync(string relativePath, bool localized, params (string Name, string Value)[] query)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (_credential.Length == 0) throw new TmdbApiKeyMissingException();
        var fields = new List<(string Name, string Value)> { ("api_key", _credential) };
        if (localized) fields.Add(("language", _language));
        fields.AddRange(query);
        var uri = new Uri(_apiRoot, relativePath + "?" + string.Join('&', fields.Select(f => Uri.EscapeDataString(f.Name) + "=" + Uri.EscapeDataString(f.Value))));
        for (var attempt = 1; ; attempt++)
        {
            CancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (IsTransient(response.StatusCode) && attempt < _attemptLimit)
                {
                    await Task.Delay(RetryDelay(attempt, response), CancellationToken).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(CancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(body, cancellationToken: CancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (attempt < _attemptLimit && (error.StatusCode is null || IsTransient(error.StatusCode.Value)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested && attempt < _attemptLimit)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleScraper/1.0");
        return client;
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    private static TimeSpan RetryDelay(int attempt, HttpResponseMessage response)
    {
        var duration = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(200 * attempt));
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : duration;
    }
    private static string SearchQuery(string query) => !string.IsNullOrWhiteSpace(query) ? query.Trim() : throw new ArgumentException(L.Text("Enter metadata search keywords."), nameof(query));
    private static void ValidateId(int id) { if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id)); }
    private static T Deserialize<T>(JsonElement element) => element.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("TMDB returned an empty metadata object.");
    private static List<T> ReadList<T>(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? Deserialize<List<T>>(values) : new();
    private static List<T> CreditList<T>(JsonElement root, string property) => root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object ? ReadList<T>(credits, property) : new();
    private static string NestedText(JsonElement root, string objectName, string property) => root.TryGetProperty(objectName, out var child) && child.ValueKind == JsonValueKind.Object ? Text(child, property) : "";
    private static string Text(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Number(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static int LogoPriority(string language, string preferred) => language.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 : language.Length == 0 ? 1 : language.Equals("en", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    private static int ReadDuration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => 0,
        JsonValueKind.Number when value.TryGetInt32(out var duration) => duration,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) => duration,
        _ => throw new JsonException("TMDB runtime must be an integer or null.")
    };
}
