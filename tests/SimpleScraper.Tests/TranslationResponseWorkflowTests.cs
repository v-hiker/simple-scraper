using System.Net;
using System.Text;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class TranslationResponseWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var episode = JsonSerializer.Deserialize<TvEpisodeMetadata>("{\"id\":13,\"episode_number\":13,\"runtime\":null}")!;
        check(episode.EpisodeNumber == 13 && episode.Runtime == 0, "null optional episode duration preserves episode identity and becomes unknown duration");
        check(JsonSerializer.Deserialize<MovieMetadata>("{\"id\":1,\"runtime\":null}")!.Runtime == 0 && JsonSerializer.Deserialize<TvEpisodeMetadata>("{\"runtime\":4}")!.Runtime == 4, "optional duration supports null and real integer values across movie and episode data");
        var invalid = false; try { JsonSerializer.Deserialize<TvEpisodeMetadata>("{\"runtime\":\"invalid\"}"); } catch (JsonException) { invalid = true; }
        check(invalid, "invalid nonnumeric duration is not silently treated as a valid API response");
        var client = new TmdbApiClient("test", "zh-CN", new HttpClient(new WorkflowHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/season/") ? """
            {"name":"Season","episodes":[{"id":1,"episode_number":1,"runtime":4},{"id":13,"episode_number":13,"runtime":null}]}
            """ : "{\"id\":99,\"name\":\"Series\",\"overview\":\"简介\",\"seasons\":[{\"season_number\":1}]}") })));
        var doc = await new TmdbMetadataProvider(client).LoadAsync("99", LibraryMediaKind.Series, Array.Empty<int>());
        check(doc.Episodes.Count == 2 && doc.Episodes.Single(e => e.Number == 13).Runtime == 0, "one missing episode duration no longer disables the whole series matching workflow");
        var text = string.Concat(Enumerable.Repeat("日本語の長い説明😀。", 90));
        var chunks = SynopsisTranslationService.Chunks(text);
        check(chunks.Count > 1 && string.Concat(chunks) == text && chunks.All(c => Encoding.UTF8.GetByteCount(c) <= 480 && !c.Contains('\uFFFD')), "translation chunks preserve complete Unicode source and respect byte limits");
        var calls = 0;
        var http = new HttpClient(new WorkflowHandler(request =>
        {
            Interlocked.Increment(ref calls);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"译文 &amp; 文本\"}}") };
        }));
        var directory = Path.Combine(temporary, "translation-cache");
        var translator = new SynopsisTranslationService(http, directory);
        var translated = await translator.TranslateAsync("日本語です。", "zh");
        check(translated == "译文 & 文本" && calls == 1 && translator.ReadCache("日本語です。", "zh-CN") == translated, "manual translation decodes text and caches under the normalized target language");
        await new SynopsisTranslationService(http, directory).TranslateAsync("日本語です。", "zh-CN");
        check(calls == 1, "opening another synopsis view reuses persisted translations without another API request");
        await translator.TranslateAsync("変更された文章。", "zh-CN"); await translator.TranslateAsync("日本語です。", "en-US");
        check(calls == 3, "changed source or target language receives a distinct translation cache entry");
        await Task.WhenAll(translator.TranslateAsync("共有の文章。", "zh-CN"), new SynopsisTranslationService(http, directory).TranslateAsync("共有の文章。", "zh-CN"));
        check(calls == 4, "concurrent translations of the same synopsis share one API call");
        translated = await translator.TranslateAsync("第一段。\n\n第二段。", "zh-CN");
        check(translated.Contains("\n\n"), "translation retains paragraph boundaries");
        var before = Directory.GetFiles(directory).Length;
        var blocked = new SynopsisTranslationService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"responseStatus\":429,\"quotaFinished\":true}") })), directory);
        invalid = false; try { await blocked.TranslateAsync("Quota sample", "zh-CN"); } catch (InvalidOperationException) { invalid = true; }
        check(invalid && Directory.GetFiles(directory).Length == before, "translation quota errors never become cached synopsis content");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        invalid = false; try { await translator.TranslateAsync("Cancelled sample", "zh-CN", cancelled.Token); } catch (OperationCanceledException) { invalid = true; }
        check(invalid && Directory.GetFiles(directory).Length == before && !Directory.GetFiles(directory, "*.tmp").Any(), "cancelled translation leaves no partial cache or temporary files");
        check(SynopsisTranslationService.KnownLanguage("今度は海に行きます。") == "ja" && SynopsisTranslationService.KnownLanguage("中文简介") == "", "Japanese synopsis can be shown as original while ambiguous Han text is not assigned a made-up language");
        check(SynopsisTranslationService.KnownLanguage("少年在学校认识了新的朋友，作品名字叫「みるタイツ」，随后大家一起参加活动，度过快乐而充实的校园生活。") == "", "a foreign title inside a localized paragraph does not disable the current-language synopsis");
        var sameLanguage = new SynopsisTranslationService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"responseStatus\":\"403\",\"responseDetails\":\"PLEASE SELECT TWO DISTINCT LANGUAGES\",\"responseData\":{\"translatedText\":\"PLEASE SELECT TWO DISTINCT LANGUAGES\"}}") })), directory);
        check(await sameLanguage.TranslateAsync("已经有中文简介。", "zh-CN") == "已经有中文简介。" && sameLanguage.ReadCache("已经有中文简介。", "zh-CN") == null, "already localized text is preserved without caching a provider error or labeling it a translation");
        var longEnglish = string.Concat(Enumerable.Repeat("Already localized synopsis. ", 50));
        check(await sameLanguage.TranslateAsync(longEnglish, "en-US") == longEnglish, "retaining already localized chunks preserves original spaces across long paragraphs");
        var mixed = new SynopsisTranslationService(new HttpClient(new WorkflowHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("已有中文") ? "{\"responseStatus\":\"403\",\"responseDetails\":\"PLEASE SELECT TWO DISTINCT LANGUAGES\"}" : "{\"responseStatus\":\"200\",\"responseData\":{\"translatedText\":\"译出的日文。\"}}") })), directory);
        check(await mixed.TranslateAsync("已有中文。\n海で遊びます。", "zh-CN") == "已有中文。\n译出的日文。", "mixed-language paragraphs retain localized sections and translate foreign sections with string response codes");
        foreach (var body in new[] { "{\"responseStatus\":\"429\"}", "{\"responseStatus\":\"403\",\"responseDetails\":\"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY\",\"quotaFinished\":true}" })
        {
            var quotaClient = new SynopsisTranslationService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) })), directory);
            invalid = false; try { await quotaClient.TranslateAsync("New quota sample", "zh-CN"); } catch (InvalidOperationException e) { invalid = e.Message == L.Text("Translation limit reached. Try again later."); }
            check(invalid, "quota failures remain explicit even when the service response status is a string");
        }
        var forbidden = new SynopsisTranslationService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"responseStatus\":\"403\",\"responseDetails\":\"OTHER FAILURE\"}") })), directory);
        invalid = false; try { await forbidden.TranslateAsync("Forbidden sample", "zh-CN"); } catch (InvalidDataException e) { invalid = e.Message.Contains("403"); }
        check(invalid, "unrelated forbidden responses are reported with the service code, not treated as already localized text");
    }

    public static async Task Live(string repository, string temporary)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "artifacts", "app", "appsettings.json")));
        var key = config.RootElement.GetProperty("TmdbApiKey").GetString() ?? throw new Exception("No configured TMDB key.");
        var doc = await new TmdbMetadataProvider(new TmdbApiClient(key, "zh-CN")).LoadAsync("86823", LibraryMediaKind.Series, Array.Empty<int>());
        if (!doc.Episodes.Any(e => e.Season == 1 && e.Number == 13)) throw new Exception("Live TMDB episode catalog incomplete.");
        Console.WriteLine($"LIVE TMDB 86823: {doc.Seasons.Count} seasons; {doc.Episodes.Count} episodes; episode 13 is available for matching; {doc.Episodes.Count(e => e.Runtime == 0)} unknown durations.");
        var service = new SynopsisTranslationService(cacheDirectory: Path.Combine(temporary, "live-translation"));
        var result = await service.TranslateAsync("海で友達と遊びます。", "zh-CN");
        if (string.IsNullOrWhiteSpace(result) || service.ReadCache("海で友達と遊びます。", "zh-CN") != result) throw new Exception("Live translation failed.");
        Console.WriteLine("LIVE MyMemory: public Japanese sample translated into Chinese and cached successfully.");
        var current = "中文介绍，少年在学校认识了新的朋友。";
        if (await service.TranslateAsync(current, "zh-CN") != current) throw new Exception("Already localized synopsis was not preserved.");
        var mixed = await service.TranslateAsync(current + "\n海で友達と遊びます。", "zh-CN");
        if (!mixed.StartsWith(current + "\n") || mixed.Contains("PLEASE SELECT")) throw new Exception("Mixed-language translation failed.");
        Console.WriteLine("LIVE MyMemory: same-language and mixed Chinese/Japanese synopsis responses handled successfully.");
    }
}
