using SimpleScraper.Models;
using SimpleScraper.Services;

internal static class TmdbCredentialsWorkflowTests
{
    public static async Task Run(string repository, Action<bool, string> check)
    {
        check(TmdbCredentials.Normalize(null) == "" && TmdbCredentials.Normalize(" personal-key ") == "personal-key", "Only the user's trimmed key is used.");
        check(TmdbCredentials.RegistrationUrl == "https://www.themoviedb.org/settings/api", "Registration opens official TMDB settings.");
        check(TmdbCredentials.NeedsConfiguration(new[] { "TMDB" }, "") && !TmdbCredentials.NeedsConfiguration(new[] { "Bangumi" }, ""), "Only credential-requiring providers request setup.");
        int calls = 0;
        using var http = new HttpClient(new WorkflowHandler(_ => { calls++; return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }; }));
        var client = new TmdbApiClient("", "zh-CN", http);
        foreach (var operation in new Func<Task>[] {
            async () => { await client.SearchMovieAsync("Example"); }, async () => { await client.SearchTvAsync("Example"); },
            async () => { await client.GetMovieDetailsAsync(1); }, async () => { await client.GetTvDetailsAsync(1); },
            async () => { await client.GetMovieCastAsync(1); }, async () => { await client.GetTvSeasonDetailsAsync(1, 1); },
            async () => { await client.GetOriginalOverviewAsync(1, true); }, async () => { await client.GetClearLogoAsync(1, true); }
        })
        {
            bool rejected = false;
            try { await operation(); } catch (TmdbApiKeyMissingException) { rejected = true; }
            check(rejected && calls == 0, "Missing API key is rejected before HTTP.");
        }
        using var bgm = new HttpClient(new WorkflowHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":1,\"name\":\"Example\",\"name_cn\":\"示例\",\"date\":\"2020-01-01\"}]}") }));
        var report = await MetadataProviderSearch.SearchAsync(new IMetadataProvider[] { new TmdbMetadataProvider(client), new BangumiMetadataProvider(bgm) }, "Example", LibraryMediaKind.Series);
        check(report.Candidates.Count == 1 && report.Errors.Count == 1, "Provider partial success remains usable.");
        var config = new ConfigService();
        config.SetApiKey(" test-personal-key ");
        check(new ConfigService().GetApiKey() == "test-personal-key", "Personal key persists across instances.");
        config.SetApiKey("");
        check(new ConfigService().GetApiKey() == "", "Empty key persists with no embedded fallback.");
    }
}
