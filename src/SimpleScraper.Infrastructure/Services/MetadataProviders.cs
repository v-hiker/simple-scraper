using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimpleScraper.Models;

namespace SimpleScraper.Services;

public interface IMetadataProvider
{
    string Name { get; }
    Task<List<MetadataCandidate>> SearchAsync(string query, LibraryMediaKind kind);
    // An empty season list loads the selected source's complete episode catalog, independent of local filenames.
    Task<MetadataDocument> LoadAsync(string id, LibraryMediaKind kind, IReadOnlyList<int> seasons);
    Task<List<MetadataCandidate>> SearchAsync(string query, LibraryMediaKind kind, CancellationToken cancellationToken) => SearchAsync(query, kind).WaitAsync(cancellationToken);
}
public sealed record ProviderSearchReport(List<MetadataCandidate> Candidates, List<string> Errors);
public static class MetadataCredits
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MetadataDocument, SemaphoreSlim> Gates = new();
    public static async Task EnsureAsync(MetadataDocument document, LibraryMediaKind kind, TmdbApiClient? tmdb, BangumiMetadataProvider? bangumi)
    {
        var gate = Gates.GetValue(document, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            if (document.CreditsLoaded) return;
            if (document.Provider == "Bangumi" && bangumi != null) await bangumi.LoadCreditsAsync(document);
            else if (document.Provider == "TMDB" && tmdb != null) await new TmdbMetadataProvider(tmdb).LoadCreditsAsync(document, kind);
        }
        finally { gate.Release(); }
    }
}
public static class MetadataProviderSearch
{
    public static async Task<ProviderSearchReport> SearchAsync(IEnumerable<IMetadataProvider> providers, string query, LibraryMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException(L.Text("Enter metadata search keywords."));
        var tasks = providers.Select(async provider =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); return new ProviderSearchReport(await provider.SearchAsync(query, kind, cancellationToken), new()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) { return new ProviderSearchReport(new(), new() { provider.Name + ": " + e.Message }); }
        }).ToList();
        if (tasks.Count == 0) throw new InvalidOperationException(L.Text("Select at least one metadata source."));
        var reports = await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        return new(reports.SelectMany(r => r.Candidates).ToList(), reports.SelectMany(r => r.Errors).ToList());
    }
}

public sealed class TmdbMetadataProvider(TmdbApiClient client) : IMetadataProvider
{
    public string Name => "TMDB";
    public async Task<List<MetadataCandidate>> SearchAsync(string query, LibraryMediaKind kind)
    {
        if (kind == LibraryMediaKind.Movie)
            return (await client.SearchMovieAsync(query)).Select(m => new MetadataCandidate(Name, m.TmdbId.ToString(), m.Title, m.OriginalTitle, m.Year, m.Overview, client.GetImageUrl(m.PosterPath, "w342"))).ToList();
        return (await client.SearchTvAsync(query)).Select(m => new MetadataCandidate(Name, m.TmdbId.ToString(), m.Name, "", m.Year, m.Overview, client.GetImageUrl(m.PosterPath, "w342"))).ToList();
    }
    public async Task<MetadataDocument> LoadAsync(string id, LibraryMediaKind kind, IReadOnlyList<int> seasons)
    {
        if (!int.TryParse(id, out var number) || number <= 0) throw new ArgumentException(L.Text("Invalid provider ID."));
        MetadataDocument doc;
        if (kind == LibraryMediaKind.Movie)
        {
            var m = await client.GetMovieDetailsAsync(number) ?? throw new InvalidDataException(L.Text("Metadata not found."));
            doc = new() { Title = m.Title, OriginalTitle = m.OriginalTitle, Date = m.ReleaseDate, Overview = m.Overview, OverviewLanguage = m.OverviewLanguage, OriginalOverview = m.OriginalOverview, OriginalOverviewLoaded = true, Rating = m.Rating, VoteCount = m.VoteCount, Tagline = m.Tagline, PosterUrl = client.GetImageUrl(m.PosterPath), BackdropUrl = client.GetImageUrl(m.BackdropPath), Genres = m.Genres.Select(g => g.Name).ToList(), Cast = m.Cast };
            doc.Crew = m.Directors.Concat(m.Writers).Concat(m.Producers).DistinctBy(p => (p.Id, p.Job)).ToList(); doc.Studios = m.ProductionCompanies.Select(s => s.Name).ToList(); doc.CreditsLoaded = true; doc.Runtime = m.Runtime;
            if (!string.IsNullOrEmpty(m.ImdbId)) doc.Ids["imdb"] = m.ImdbId;
        }
        else
        {
            var m = await client.GetTvDetailsAsync(number) ?? throw new InvalidDataException(L.Text("Metadata not found."));
            doc = new() { Title = m.Name, OriginalTitle = m.OriginalName, Date = m.FirstAirDate, Overview = m.Overview, OverviewLanguage = m.OverviewLanguage, OriginalOverview = m.OriginalOverview, OriginalOverviewLoaded = true, Rating = m.Rating, VoteCount = m.VoteCount, Tagline = m.Tagline, PosterUrl = client.GetImageUrl(m.PosterPath), BackdropUrl = client.GetImageUrl(m.BackdropPath), Genres = m.Genres.Select(g => g.Name).ToList(), Cast = m.Cast };
            doc.Crew = m.Crew; doc.Studios = m.ProductionCompanies.Concat(m.Networks).Select(s => s.Name).Distinct().ToList(); doc.CreditsLoaded = true; doc.Runtime = m.EpisodeRunTime;
            if (!string.IsNullOrEmpty(m.ImdbId)) doc.Ids["imdb"] = m.ImdbId;
            var requested = seasons.Count == 0 ? m.Seasons.Select(s => s.Number).Where(s => s >= 0).Distinct().ToList() : seasons.Distinct().ToList();
            using var parallelism = new SemaphoreSlim(4);
            var seasonReports = await Task.WhenAll(requested.Select(async season =>
            {
                await parallelism.WaitAsync(client.CancellationToken);
                try { return await client.GetTvSeasonDetailsAsync(number, season); }
                finally { parallelism.Release(); }
            }));
            doc.Episodes.AddRange(seasonReports.SelectMany(s => s.Episodes.Select(e => new MetadataEpisode(e.TmdbId.ToString(), s.Number, e.EpisodeNumber, e.Name, e.Overview, e.AirDate, e.Runtime, client.GetImageUrl(e.StillPath)) { Rating = e.Rating, VoteCount = e.VoteCount })));
            doc.Seasons.AddRange(seasonReports.Select(s => new MetadataSeason(s.Number, s.Name, s.Overview, s.AirDate, client.GetImageUrl(s.PosterPath ?? m.Seasons.FirstOrDefault(c => c.Number == s.Number)?.PosterPath)) { Rating = s.Rating }));
        }
        doc.Provider = Name; doc.Id = id; doc.Ids["tmdb"] = id;
        return doc;
    }
    public async Task LoadCreditsAsync(MetadataDocument document, LibraryMediaKind kind)
    {
        if (document.CreditsLoaded) return;
        if (!int.TryParse(document.Id, out var id) || id <= 0) throw new ArgumentException(L.Text("Invalid provider ID."));
        if (kind == LibraryMediaKind.Series)
        {
            var source = await client.GetTvDetailsAsync(id) ?? throw new InvalidDataException(L.Text("Metadata not found."));
            document.Cast = source.Cast; document.Crew = source.Crew; document.Studios = source.ProductionCompanies.Concat(source.Networks).Select(s => s.Name).Distinct().ToList();
        }
        else
        {
            var source = await client.GetMovieDetailsAsync(id) ?? throw new InvalidDataException(L.Text("Metadata not found."));
            document.Cast = source.Cast; document.Crew = source.Directors.Concat(source.Writers).Concat(source.Producers).DistinctBy(p => (p.Id, p.Job)).ToList(); document.Studios = source.ProductionCompanies.Select(s => s.Name).ToList();
        }
        document.CreditsLoaded = true;
    }
}

public sealed class BangumiMetadataProvider : IMetadataProvider
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient _http;
    private readonly CancellationToken _cancellation;
    public string Name => "Bangumi";
    public BangumiMetadataProvider(HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        _http = http ?? SharedHttp; _cancellation = cancellationToken;
    }
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleScraper/0.1.0"); return http;
    }
    public async Task<List<MetadataCandidate>> SearchAsync(string query, LibraryMediaKind kind)
    {
        using var response = await _http.PostAsJsonAsync("https://api.bgm.tv/v0/search/subjects?limit=30&offset=0", new { keyword = query.Trim(), sort = "match", filter = new { type = new[] { 2, 6 } } }, _cancellation);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").EnumerateArray().Select(e =>
        {
            var title = Text(e, "name_cn"); if (title.Length == 0) title = Text(e, "name");
            var date = Text(e, "date");
            return new MetadataCandidate(Name, e.GetProperty("id").ToString(), title, Text(e, "name"), DateTime.TryParse(date, out var d) ? d.Year : 0, Text(e, "summary"), Image(e));
        }).ToList();
    }
    public async Task<MetadataDocument> LoadAsync(string id, LibraryMediaKind kind, IReadOnlyList<int> seasons)
    {
        if (!int.TryParse(id, out var number) || number <= 0) throw new ArgumentException(L.Text("Invalid provider ID."));
        if (kind == LibraryMediaKind.Series && seasons.Count > 1) throw new InvalidOperationException(L.Text("Bangumi requires one subject per local season."));
        using var subject = await GetAsync($"subjects/{number}");
        var root = subject.RootElement;
        var title = Text(root, "name_cn"); if (title.Length == 0) title = Text(root, "name");
        var doc = new MetadataDocument { Provider = Name, Id = id, Title = title, OriginalTitle = Text(root, "name"), Date = Text(root, "date"), Overview = Text(root, "summary"), PosterUrl = Image(root), Ids = new() { ["bangumi"] = id } };
        if (root.TryGetProperty("rating", out var rating) && rating.TryGetProperty("score", out var score)) doc.Rating = score.GetDouble();
        if (kind == LibraryMediaKind.Series)
        {
            doc.Seasons.Add(new(seasons.Count == 0 ? 1 : Math.Max(1, seasons[0]), doc.Title, doc.Overview, doc.Date, doc.PosterUrl));
            var specialNumber = 0;
            foreach (var episodeType in new[] { 0, 1 })
            for (var offset = 0; ; offset += 100)
            {
                using var page = await GetAsync($"episodes?subject_id={number}&type={episodeType}&limit=100&offset={offset}");
                var data = page.RootElement.GetProperty("data").EnumerateArray().ToList();
                foreach (var e in data)
                {
                    // 'ep' is local numbering; 'sort' in a sequel may be the series' absolute number (23...44).
                    var ep = e.TryGetProperty("ep", out var local) && local.ValueKind == JsonValueKind.Number ? local.GetDouble() : e.GetProperty("sort").GetDouble();
                    if (e.GetProperty("type").GetInt32() != episodeType) continue;
                    if (!double.IsFinite(ep)) continue;
                    var special = episodeType == 1 || ep != Math.Truncate(ep);
                    if (!special && (ep <= 0 || ep > 9999)) continue;
                    var name = Text(e, "name_cn"); if (name.Length == 0) name = Text(e, "name");
                    var runtime = e.TryGetProperty("duration_seconds", out var duration) ? duration.GetInt32() / 60 : 0;
                    // Special numbering is a proposed S00 order, retained by provider episode ID and reviewed in the comparison screen.
                    doc.Episodes.Add(new(e.GetProperty("id").ToString(), special ? 0 : seasons.Count == 0 ? 1 : Math.Max(1, seasons[0]), special ? ++specialNumber : (int)ep, name, Text(e, "desc"), Text(e, "airdate"), runtime, "", special ? ep.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", Text(e, "name")));
                }
                if (data.Count < 100) break;
            }
        }
        await LoadCreditsAsync(doc);
        return doc;
    }
    public async Task LoadCreditsAsync(MetadataDocument document)
    {
        if (document.CreditsLoaded) return;
        if (!int.TryParse(document.Id, out var id) || id <= 0) throw new ArgumentException(L.Text("Invalid provider ID."));
        var completed = await Task.WhenAll(LoadCharacters(), LoadPersons());
        document.CreditsLoaded = completed.All(ok => ok);

        async Task<bool> LoadCharacters()
        {
            try
            {
                using var json = await GetAsync($"subjects/{id}/characters");
                var cast = new List<CastMember>();
                foreach (var character in json.RootElement.EnumerateArray().OrderBy(c => Text(c, "relation") == "主角" ? 0 : Text(c, "relation") == "配角" ? 1 : 2))
                {
                    if (!character.TryGetProperty("actors", out var actors) || actors.ValueKind != JsonValueKind.Array) continue;
                    foreach (var actor in actors.EnumerateArray())
                    {
                        var name = Text(actor, "name"); if (name.Length == 0) continue;
                        cast.Add(new() { Id = actor.TryGetProperty("id", out var personId) ? personId.GetInt32() : 0, Name = name, Character = Text(character, "name"), ProfilePath = Image(actor), Order = cast.Count });
                    }
                }
                document.Cast = cast.GroupBy(a => (a.Id, a.Name)).Select(g => new CastMember { Id = g.Key.Id, Name = g.Key.Name, Character = string.Join(" / ", g.Select(a => a.Character).Where(s => s.Length > 0).Distinct()), ProfilePath = g.Select(a => a.ProfilePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)), Order = g.Min(a => a.Order) }).ToList();
                lock (document.Warnings) document.Warnings.RemoveAll(w => w == L.Text("Bangumi cast information could not be loaded. Existing matches are preserved."));
                return true;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { lock (document.Warnings) document.Warnings.Add(L.Text("Bangumi cast information could not be loaded. Existing matches are preserved.")); return false; }
        }
        async Task<bool> LoadPersons()
        {
            try
            {
                using var json = await GetAsync($"subjects/{id}/persons");
                var crew = new List<CrewMember>(); var studios = new List<string>();
                foreach (var person in json.RootElement.EnumerateArray())
                {
                    var name = Text(person, "name"); if (name.Length == 0) continue;
                    var job = Text(person, "relation");
                    if (job is "动画制作" or "制作" or "製作") studios.Add(name);
                    if (person.TryGetProperty("type", out var type) && type.GetInt32() == 2) continue;
                    crew.Add(new() { Id = person.TryGetProperty("id", out var personId) ? personId.GetInt32() : 0, Name = name, Job = job, ProfilePath = Image(person) });
                }
                document.Crew = crew.DistinctBy(p => (p.Id, p.Name, p.Job)).ToList(); document.Studios = studios.Distinct().ToList();
                lock (document.Warnings) document.Warnings.RemoveAll(w => w == L.Text("Bangumi production credits could not be loaded. Existing matches are preserved."));
                return true;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { lock (document.Warnings) document.Warnings.Add(L.Text("Bangumi production credits could not be loaded. Existing matches are preserved.")); return false; }
        }
    }
    private async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await _http.GetAsync("https://api.bgm.tv/v0/" + path, _cancellation);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Image(JsonElement e) => e.TryGetProperty("images", out var images) ? Text(images, "large") : "";
}
