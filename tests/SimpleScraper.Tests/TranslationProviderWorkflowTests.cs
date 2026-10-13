using System.Net;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class TranslationProviderWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var defaults = JsonSerializer.Deserialize<AppConfig>("{}")!.Translation;
        check(defaults.Provider == "MyMemory" && defaults.MyMemoryEmail == "", "old app settings keep anonymous MyMemory without sending an email");
        check(TranslationProviders.Options.Select(o => o.Id).Distinct().Count() == 4, "translation registry exposes four independent selectable providers including keyless Google");
        var directory = Path.Combine(temporary, "provider-cache");
        var calls = 0;
        using var google = new HttpClient(new TranslationHandler(async (request, token) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            check(request.Method == HttpMethod.Post && request.RequestUri!.Host == "translation.googleapis.com" && request.RequestUri.AbsolutePath == "/language/translate/v2" && request.RequestUri.Query == "?key=test%26key", "Google uses the official Basic endpoint and safely escaped configured key");
            check(body.RootElement.GetProperty("target").GetString() == "zh-CN" && body.RootElement.GetProperty("format").GetString() == "text" && body.RootElement.GetProperty("model").GetString() == "nmt" && !body.RootElement.TryGetProperty("source", out _), "Google selects NMT plain text and automatic source detection");
            return Json("{\"data\":{\"translations\":[{\"translatedText\":\"谷歌译文 &amp; 文本\"}]}}");
        }));
        var settings = new TranslationSettings { Provider = "GoogleCloud", GoogleApiKey = "test&key" };
        var service = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(settings, google));
        check(await service.TranslateAsync("日本語です。", "zh-CN") == "谷歌译文 & 文本", "Google response produces decoded synopsis text");
        await service.TranslateAsync("日本語です。", "zh-CN");
        check(calls == 1, "Google synopsis cache avoids repeated billed requests");
        using var myMemory = new HttpClient(new WorkflowHandler(request =>
        {
            check(!request.RequestUri!.Query.Contains("&de="), "anonymous MyMemory sends no personal email");
            return Json("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"MyMemory 译文\"}}");
        }));
        var memoryService = new SynopsisTranslationService(myMemory, directory);
        check(await memoryService.TranslateAsync("日本語です。", "zh-CN") == "MyMemory 译文" && service.ReadCache("日本語です。", "zh-CN") == "谷歌译文 & 文本", "switching providers never mixes or replaces another engine's cached translation");
        using var emailHttp = new HttpClient(new WorkflowHandler(request =>
        {
            check(request.RequestUri!.Query.Contains("&de=reader%40example.com"), "MyMemory sends the email parameter only after explicit configuration");
            return Json("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"邮箱译文\"}}");
        }));
        await new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(new() { MyMemoryEmail = "reader@example.com" }, emailHttp)).TranslateAsync("別の文章。", "zh-CN");
        using var libre = new HttpClient(new TranslationHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            check(request.RequestUri!.AbsolutePath == "/api/translate" && body.RootElement.GetProperty("source").GetString() == "auto" && body.RootElement.GetProperty("target").GetString() == "zt" && !body.RootElement.TryGetProperty("api_key", out _), "LibreTranslate preserves custom base paths, maps Traditional Chinese and omits an empty optional key");
            return Json("{\"translatedText\":\"繁體譯文\"}");
        }));
        var libreSettings = new TranslationSettings { Provider = "LibreTranslate", LibreEndpoint = "http://localhost:5000/api/" };
        var libreService = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(libreSettings, libre));
        check(await libreService.TranslateAsync("日本語です。", "zh-TW") == "繁體譯文", "custom LibreTranslate response is available through the same translation workflow");
        var otherEndpoint = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(libreSettings with { LibreEndpoint = "http://localhost:5001/api" }, libre));
        check(otherEndpoint.ReadCache("日本語です。", "zh-TW") == null, "different self-hosted translation endpoints have isolated caches");
        var longText = string.Concat(Enumerable.Repeat("日本語の長い説明。", 180));
        var googleChunks = SynopsisTranslationService.Chunks(longText, 12000);
        check(googleChunks.Count == 1 && string.Concat(googleChunks) == longText, "Google keeps ordinary long synopsis context without MyMemory's 500-byte splitting restriction");
        foreach (var invalidSettings in new[] { settings with { GoogleApiKey = "" }, libreSettings with { LibreEndpoint = "file:///secret" }, libreSettings with { LibreEndpoint = "https://user:pass@example.com" }, new TranslationSettings { MyMemoryEmail = "invalid-email" } })
        {
            var rejected = false; try { TranslationProviders.Create(invalidSettings).Validate(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "invalid translation configuration is rejected before any HTTP request");
        }
        var before = Directory.GetFiles(directory).Length;
        using var denied = new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("private provider error including test&key") }));
        var deniedService = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(settings, denied));
        var failed = false; try { await deniedService.TranslateAsync("Denied source", "zh-CN"); } catch (InvalidOperationException e) { failed = !e.Message.Contains("test&key") && e.Message == L.Text("Translation access denied. Check the API key and service settings."); }
        check(failed && Directory.GetFiles(directory).Length == before, "credential denial reports a safe actionable message and writes no failed translation cache");
        using var badResponse = new HttpClient(new WorkflowHandler(_ => Json("{\"data\":{\"translations\":[]}}")));
        failed = false; try { await new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(settings, badResponse)).TranslateAsync("Malformed source", "zh-CN"); } catch (InvalidDataException) { failed = true; }
        check(failed && Directory.GetFiles(directory).Length == before, "invalid Google result is never mistaken for a successful synopsis");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelledHttp = new HttpClient(new TranslationHandler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Json("{}"); }));
        using var cancellation = new CancellationTokenSource();
        var pending = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(settings, cancelledHttp)).TranslateAsync("Cancel source", "zh-CN", cancellation.Token);
        await started.Task; cancellation.Cancel(); failed = false; try { await pending; } catch (OperationCanceledException) { failed = true; }
        check(failed && Directory.GetFiles(directory).Length == before, "cancelling the view cancels the provider HTTP operation and keeps the cache clean");
        check(Directory.GetFiles(directory).All(file => !File.ReadAllText(file).Contains("test&key") && !File.ReadAllText(file).Contains("reader@example.com")), "translation cache stores no API key or configured email");
        using var googleWeb = new HttpClient(new WorkflowHandler(request =>
        {
            check(request.RequestUri!.Host == "translate.googleapis.com" && request.RequestUri.AbsolutePath == "/translate_a/single" && request.RequestUri.Query.Contains("sl=auto") && request.RequestUri.Query.Contains("tl=zh-CN") && !request.RequestUri.Query.Contains("key="), "Google web translation sends no API key and uses automatic detection");
            return Json("[[[\"免费谷歌译文。\",\"source\"],[\"第二句。\",\"source\"]],null,\"ja\"]");
        }));
        var webService = new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(new() { Provider = "GoogleTranslate" }, googleWeb));
        check(await webService.TranslateAsync("日本語です。", "zh-CN") == "免费谷歌译文。第二句。" && service.ReadCache("日本語です。", "zh-CN") == "谷歌译文 & 文本", "keyless Google joins all translated sentence segments and isolates its cache from Cloud");
        var webBefore = Directory.GetFiles(directory).Length;
        using var blockedWeb = new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.Forbidden)));
        failed = false; try { await new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(new() { Provider = "GoogleTranslate" }, blockedWeb)).TranslateAsync("Web forbidden", "zh-CN"); } catch (InvalidOperationException e) { failed = e.Message == L.Text("Translation service unavailable. Try another source."); }
        check(failed && Directory.GetFiles(directory).Length == webBefore, "blocked keyless Google offers another source instead of incorrectly requesting an API key");
        using var limitedWeb = new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.TooManyRequests)));
        failed = false; try { await new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(new() { Provider = "GoogleTranslate" }, limitedWeb)).TranslateAsync("Web rate limited", "zh-CN"); } catch (InvalidOperationException e) { failed = e.Message == L.Text("Google Translate is temporarily rate limited. Try later or choose another source."); }
        check(failed && Directory.GetFiles(directory).Length == webBefore, "Google web rate limiting is reported as temporary throttling rather than a claimed daily quota exhaustion");
        using var invalidWeb = new HttpClient(new WorkflowHandler(_ => Json("[null,null,\"ja\"]")));
        failed = false; try { await new SynopsisTranslationService(cacheDirectory: directory, provider: TranslationProviders.Create(new() { Provider = "GoogleTranslate" }, invalidWeb)).TranslateAsync("Web malformed", "zh-CN"); } catch (InvalidDataException) { failed = true; }
        check(failed && Directory.GetFiles(directory).Length == webBefore, "malformed keyless web responses do not overwrite valid translation caches");
        var config = new ConfigService(); var previous = config.GetTranslationSettings();
        try { config.SetTranslationSettings(settings with { LibreEndpoint = "http://localhost:5000", LibreApiKey = "fixture" }); var saved = config.GetTranslationSettings(); check(saved.Provider == "GoogleCloud" && saved.GoogleApiKey == "test&key" && saved.LibreApiKey == "fixture", "saving the selected engine retains other configured engine credentials"); }
        finally { config.SetTranslationSettings(previous); }
    }
    public static async Task Live(string temporary)
    {
        var text = "今度は海で友達と遊びます。";
        var service = new SynopsisTranslationService(cacheDirectory: Path.Combine(temporary, "live-google-web"), provider: TranslationProviders.Create(new() { Provider = "GoogleTranslate" }));
        var translated = await service.TranslateAsync(text, "zh-CN");
        if (string.IsNullOrWhiteSpace(translated) || translated == text || service.ReadCache(text, "zh-CN") != translated) throw new Exception("Live keyless Google translation failed.");
        Console.WriteLine("LIVE Google Translate (no key): public Japanese sample translated into Chinese and cached successfully.");
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class TranslationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
