using System.Net;
using System.Text;
using SimpleScraper.Services;

internal static class AdapterContractTests
{
    public static async Task Run(string folder, Action<bool, string> check)
    {
        HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        string query = "";
        using var http = new HttpClient(new WorkflowHandler(r => { query = r.RequestUri!.Query; return Json("{\"results\":[]}"); }));
        await new TmdbApiClient("key", "zh-CN", http).SearchMovieAsync("A&B", 0);
        check(query.Contains("language=zh-CN") && !query.Contains("primary_release_year") && query.Contains("A%26B"), "TMDB encodes keywords and omits unknown year.");
        using var denied = new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.Unauthorized)));
        bool hidden = false;
        try { await new TmdbApiClient("secret-fixture", "zh-CN", denied).SearchMovieAsync("Example"); }
        catch (Exception e) { hidden = !e.Message.Contains("secret-fixture"); }
        check(hidden, "API failures conceal credentials.");
        using var fallback = new HttpClient(new WorkflowHandler(r => r.RequestUri!.Query.Contains("language=en-US")
            ? Json("{\"id\":3,\"title\":\"English\",\"overview\":\"Original plot\"}")
            : Json("{\"id\":3,\"title\":\"中文标题\",\"overview\":\"\"}")));
        var movie = await new TmdbApiClient("key", "zh-CN", fallback).GetMovieDetailsAsync(3);
        check(movie?.Title == "中文标题" && movie.Overview == "Original plot", "Plot fallback preserves the localized title.");
        int attempts = 0;
        using var imageHttp = new HttpClient(new WorkflowHandler(_ => ++attempts < 3 ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 255, 216, 255, 217 }) }));
        var target = Path.Combine(folder, "adapter.jpg");
        var images = new ImageDownloadService(imageHttp);
        check(await images.DownloadAnyImageAsync("https://test.invalid/image", target) && attempts == 3, "Transient image failure retries before committing.");
        var original = File.ReadAllBytes(target);
        check(await images.DownloadAnyImageAsync("https://test.invalid/image", target) && attempts == 3 && original.SequenceEqual(File.ReadAllBytes(target)), "Existing artwork is preserved without HTTP.");
        using var html = new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>not a PNG</html>") }));
        var png = Path.Combine(folder, "rejected.png");
        check(!await new ImageDownloadService(html).DownloadPngAsync("https://test.invalid/image", png) && !File.Exists(png), "PNG logo validation rejects HTML.");
        check(!Directory.EnumerateFiles(folder, "*.download", SearchOption.AllDirectories).Any(), "Downloads clean up temporary files.");
        var config = new ConfigService();
        config.SetScrapeLanguage("zh");
        check(new ConfigService().GetScrapeLanguage() == "zh-CN", "Legacy language alias normalizes.");
        config.SetUiLanguage("en-US");
        check(new ConfigService().GetUiLanguage() == "en-US", "UI language persists across instances.");
        new LibraryStateStore { Profile = "Kodi" }.Save();
        check(LibraryStateStore.Load().Profile == "Kodi", "Independent data directory stores library state.");
        check(AppDataPaths.Root.StartsWith(folder, StringComparison.OrdinalIgnoreCase), "Tests isolate default application data.");
    }
}
