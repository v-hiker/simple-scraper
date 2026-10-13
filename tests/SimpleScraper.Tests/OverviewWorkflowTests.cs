using System.Net;
using System.Text.Json;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class OverviewWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var cached = "中文简介。\r\n第二段。\r\n\r\n[简介原文]\r\n日本語の紹介。\r\n第二段。";
        var versions = OverviewText.Split(cached);
        check(versions.Current == "中文简介。\n第二段。" && versions.Original == "日本語の紹介。\n第二段。", "cached bilingual synopsis separates versions without headings or duplicate paragraphs");
        check(OverviewText.Split("This story mentions [简介原文] inside a sentence.").Original == "", "synopsis words inside a paragraph are never mistaken for a language heading");
        check(OverviewText.Split("【原文】\nOnly original").Current == "" && OverviewText.Split("【原文】\nOnly original").Original == "Only original", "original-only synopsis preserves its available text");
        check(OverviewText.Split(cached, "Explicit original").Original == "Explicit original" && OverviewText.Split("").Current == "", "separate original field takes precedence and empty synopsis stays empty");
        using var translated = JsonDocument.Parse("""
            {"original_language":"ja","overview":"中文剧情","translations":{"translations":[
            {"iso_639_1":"en","data":{"overview":"English translation"}},
            {"iso_639_1":"ja","data":{"overview":""}},
            {"iso_639_1":"ja","data":{"overview":"日本語の物語"}}]}}
            """);
        check(OverviewText.TmdbOriginal(translated.RootElement, "zh-CN") == "日本語の物語", "TMDB original synopsis uses the declared original language and ignores empty translations");
        using var noTranslation = JsonDocument.Parse("{\"original_language\":\"ja\",\"overview\":\"Text\"}");
        check(OverviewText.TmdbOriginal(noTranslation.RootElement, "zh-CN") == "" && OverviewText.TmdbOriginal(noTranslation.RootElement, "ja-JP") == "Text", "missing translations never mislabel a localized synopsis as original");
        var requests = new List<string>();
        var client = new TmdbApiClient("test", "zh-CN", new HttpClient(new WorkflowHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"id":99,"title":"Localized movie","name":"Localized series","overview":"中文剧情","original_language":"ja","seasons":[],
                "translations":{"translations":[{"iso_639_1":"ja","data":{"overview":"日本語の物語"}}]}}
                """) };
        })));
        var provider = new TmdbMetadataProvider(client);
        foreach (var kind in new[] { LibraryMediaKind.Movie, LibraryMediaKind.Series })
        {
            var document = await provider.LoadAsync("99", kind, Array.Empty<int>());
            check(document.Overview == "中文剧情" && document.OriginalOverview == "日本語の物語" && document.OriginalOverviewLoaded, "TMDB matching retains localized and original synopsis as separate cached fields");
            var restored = JsonSerializer.Deserialize<MetadataDocument>(JsonSerializer.Serialize(document))!;
            check(restored.OriginalOverview == document.OriginalOverview && restored.Overview == document.Overview, "synopsis language versions survive saved matching records");
            check(await client.GetOriginalOverviewAsync(99, kind == LibraryMediaKind.Series) == "日本語の物語", "legacy matching record can load the original synopsis without changing current metadata");
        }
        check(requests.All(r => r.Contains("translations") && r.Contains("language=zh-CN")) && requests.Count == 4, "original-language retrieval reuses batched detail requests and preserves configured language");
        var folder = Path.Combine(temporary, "silent-missing-artwork"); Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Movie.mkv"); File.WriteAllText(video, "isolated video");
        var doc = new MetadataDocument { Provider = "Bangumi", Id = "99", Title = "Movie", CreditsLoaded = true };
        var media = new LibraryMedia { Folder = folder, Title = "Movie", Kind = LibraryMediaKind.Movie, Files = new() { new() { Path = video } }, Matches = new() { [0] = new(new("Bangumi", "99", "Movie", "", 0, "", ""), doc) } };
        var report = await new LibraryOutputService(new ImageDownloadService(), peopleDirectory: Path.Combine(temporary, "silent-people")).ScrapeAsync(media, MediaOutputProfile.Jellyfin);
        check(report.Written == 1 && report.Warnings.Count == 0 && report.Images == 0, "missing optional source artwork quietly exports metadata without explanatory warnings");
        doc.ClearLogoUrl = "https://test/broken.png";
        report = await new LibraryOutputService(new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.ServiceUnavailable)))), peopleDirectory: Path.Combine(temporary, "silent-people")).ScrapeAsync(media, MediaOutputProfile.Jellyfin);
        check(report.Warnings.Any(w => w.Contains("clearlogo.png")), "real artwork download failures still appear in scrape results");
    }
}
