using System.Net;
using System.Text;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class InfrastructureWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var attempts = 0;
        using (var http = new HttpClient(new Handler(_ => ++attempts < 3
            ? new(HttpStatusCode.ServiceUnavailable)
            : Json("{\"results\":[{\"id\":7,\"title\":\"Recovered\",\"release_date\":\"2026-01-01\"}]}"))))
        {
            var results = await new TmdbApiClient("test", "zh-CN", http).SearchMovieAsync("recovery");
            check(attempts == 3 && results.Single().TmdbId == 7, "TMDB retries transient failures and returns the successful response");
        }
        attempts = 0;
        using (var http = new HttpClient(new Handler(_ => { attempts++; return new(HttpStatusCode.Unauthorized); })))
        {
            var failed = false;
            try { await new TmdbApiClient("test", "zh-CN", http).SearchTvAsync("credentials"); }
            catch (HttpRequestException error) { failed = error.StatusCode == HttpStatusCode.Unauthorized; }
            check(failed && attempts == 1, "TMDB authentication errors are not repeatedly retried");
        }
        var path = Path.Combine(temporary, "infrastructure-settings", "preferences.json");
        var settings = new ConfigService(path);
        check(settings.GetUiLanguage() == "zh-CN" && settings.GetApiKey() == "" && !File.Exists(path), "fresh config defaults to Chinese with no bundled key and no implicit file mutation");
        await Task.WhenAll(
            Task.Run(() => new ConfigService(path).SetApiKey(" personal-key ")),
            Task.Run(() => new ConfigService(path).SetScrapeLanguage("ja")),
            Task.Run(() => new ConfigService(path).SetUiTheme("Dark")),
            Task.Run(() => new ConfigService(path).SetDefaultMetadataSources(new[] { "bangumi", "TMDB", "tmdb" })));
        check(settings.GetApiKey() == "personal-key" && settings.GetScrapeLanguage() == "ja" && settings.GetUiTheme() == "dark" && settings.GetDefaultMetadataSources().SequenceEqual(new[] { "Bangumi", "TMDB" }), "independent config instances share atomic updates without losing other preferences");
        var bytes = File.ReadAllBytes(path);
        check(!(bytes.Length > 2 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) && !Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "settings commit as UTF-8 without BOM and leave no temporary files");
        settings.SetLastTemplate("User filename template");
        settings.SavePreferences(new AppConfig { TmdbApiKey = "updated-key", UiTheme = "light", UiLanguage = "en-US", ScrapeLanguage = "en", DefaultMetadataSources = new() { "Bangumi" }, DefaultOutputProfile = "Emby", Translation = new() { Provider = "GoogleCloud", GoogleApiKey = "fixture" } });
        check(settings.GetApiKey() == "updated-key" && settings.GetScrapeLanguage() == "en-US" && settings.GetDefaultOutputProfile() == "Emby" && settings.GetTranslationSettings().GoogleApiKey == "fixture" && settings.GetLastTemplate() == "User filename template", "a single settings-page commit saves all preferences and preserves workflow templates");
        File.WriteAllText(path, "{invalid", new UTF8Encoding(false));
        var corrupt = false;
        try { settings.SetUiLanguage("en-US"); } catch (InvalidDataException) { corrupt = true; }
        check(corrupt && File.ReadAllText(path) == "{invalid", "a malformed settings file is reported and preserved rather than silently overwritten");

        var imageFolder = Path.Combine(temporary, "infrastructure-images"); Directory.CreateDirectory(imageFolder);
        var preserved = Path.Combine(imageFolder, "poster.jpg"); File.WriteAllText(preserved, "user artwork");
        attempts = 0;
        using (var http = new HttpClient(new Handler(_ => { attempts++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }; })))
            check(await new ImageDownloadService(http).DownloadAnyImageAsync("https://test/poster", preserved) && File.ReadAllText(preserved) == "user artwork" && attempts == 0, "artwork downloads preserve an existing user image before contacting the network");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        foreach (var payload in new[] { png[..^4], png.Select((b, i) => i == 20 ? (byte)(b ^ 1) : b).ToArray() })
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var invalid = Path.Combine(imageFolder, "broken.png");
            check(!await new ImageDownloadService(http).DownloadPngAsync("https://test/broken", invalid) && !File.Exists(invalid), "PNG validation rejects truncated or corrupted chunks before publishing a file");
        }
        check(!Directory.GetFiles(imageFolder, "*.download").Any(), "rejected image transfers clean up staging files");
        using var cancel = new CancellationTokenSource();
        using var waiting = new WaitingHandler(); using var waitingHttp = new HttpClient(waiting);
        var pending = new ImageDownloadService(waitingHttp, cancel.Token).DownloadAnyImageAsync("https://test/wait", Path.Combine(imageFolder, "cancel.jpg"));
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        var stopped = false; try { await pending; } catch (OperationCanceledException) { stopped = true; }
        check(stopped && waiting.Cancelled && !File.Exists(Path.Combine(imageFolder, "cancel.jpg")), "image cancellation reaches the HTTP transport and leaves no completed destination");
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return new(); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}
