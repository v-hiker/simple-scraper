using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class EpisodeWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        var folder = Path.Combine(temporary, "Episode-scope"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "S01E01.mkv", "S01E03.mkv", "SP01.mkv", "S01E11.5.mkv" }) File.WriteAllText(Path.Combine(folder, name), "isolated fixture");
        Directory.CreateDirectory(Path.Combine(folder, "OPED")); File.WriteAllText(Path.Combine(folder, "OPED", "S01E02.mkv"), "opening");
        var scanner = new LibraryScanner(); var media = scanner.Scan(folder, LibraryMediaKind.Series, false).Single();
        check(media.Files.Single(f => f.Name == "SP01.mkv").Season == 0 && media.Files.Single(f => f.Name == "SP01.mkv").Episode == 1, "SP filename becomes a reviewable S00 row");
        check(media.Files.Single(f => f.Path.Contains("OPED")).Exclusion.Length > 0, "Windows OPED directory excludes even regular-looking filenames");
        var cancelled = false; using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); try { scanner.Scan(folder, LibraryMediaKind.Series, false, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; } }
        check(cancelled, "cancelled scanning stops before filesystem traversal");
        var doc = new MetadataDocument { Provider = "Bangumi", Id = "first", Title = "测试剧集", Episodes = new() { new("a", 1, 1, "第一话", "plot", "", 0, ""), new("b", 1, 2, "第二话", "", "", 0, ""), new("c", 2, 1, "第二季第一话", "", "", 0, ""), new("sp", 0, 1, "特别篇", "", "", 0, "") } };
        var absolute = EpisodeMatching.Suggest(media, new[] { doc }, new HashSet<int> { 1 }, true);
        var third = media.Files.Single(f => f.Name == "S01E03.mkv");
        check(absolute[third.Path].Season == 2 && absolute[third.Path].Number == 1 && absolute[third.Path].Episode.Id == "c", "absolute order proposes canonical season numbering while excluding specials");
        var aired = EpisodeMatching.Suggest(media, new[] { doc }, new HashSet<int> { 0, 1 }, false);
        check(aired.Count == 2 && aired.Values.Any(b => b.Episode.Id == "sp"), "aired order matches regular and S00 numbering but leaves unmatched files alone");
        var alternate = new MetadataDocument { Provider = "TMDB", Id = "another", Episodes = doc.Episodes };
        check(EpisodeMatching.Suggest(media, new[] { doc, alternate }, new HashSet<int> { 1 }, false).Count == 0, "multiple providers never silently settle ambiguous episode candidates");
        media.Documents[EpisodeMatching.Key(alternate, LibraryMediaKind.Series)] = alternate;
        var selectedResult = EpisodeMatching.SuggestForResult(media, doc, new HashSet<int> { 1 }, null, false);
        check(selectedResult.Count == 1 && selectedResult.Values.All(b => b.SourceKey == EpisodeMatching.Key(doc, LibraryMediaKind.Series)), "result-scoped suggestions ignore other stored provider documents");
        var secondSeason = new LibraryMedia { Kind = LibraryMediaKind.Series, Files = new() { new() { Path = "S02E01.mkv", Season = 2, Episode = 1 }, new() { Path = "S01E01.mkv", Season = 1, Episode = 1 } } };
        var remapped = EpisodeMatching.SuggestForResult(secondSeason, doc, new HashSet<int> { 2 }, 1, false);
        check(remapped.Count == 1 && remapped["S02E01.mkv"].Episode.Id == "a" && remapped["S02E01.mkv"].Season == 2, "source season can map to a different local season without replacing other seasons");
        var ambiguousResult = new MetadataDocument { Provider = "TMDB", Id = "ambiguous", Episodes = new() { doc.Episodes[0], doc.Episodes[0] with { Id = "duplicate" } } };
        check(EpisodeMatching.SuggestForResult(secondSeason, ambiguousResult, new HashSet<int> { 2 }, 1, false).Count == 0, "ambiguous numbering within the selected result remains unmatched");
        var key = EpisodeMatching.Key(doc, LibraryMediaKind.Series); var file = media.Files.Single(f => f.Name == "S01E11.5.mkv"); file.Exclusion = ""; file.Season = 0; file.Episode = 2;
        media.Documents[key] = doc; media.EpisodeBindings[file.Path] = new(key, doc.Episodes.Last(), 0, 2); media.Matches[1] = new(new(doc.Provider, doc.Id, doc.Title, "", 0, "", ""), doc);
        check(EpisodeMatching.Resolve(media, file)?.Episode.Id == "sp", "manual fractional-file binding keeps provider identity with a separately chosen target number");
        var duplicateSource = new Dictionary<string, EpisodeBinding>(media.EpisodeBindings) { [third.Path] = new(key, doc.Episodes.Last(), 0, 3) };
        check(EpisodeMatching.Conflicts(duplicateSource).Count == 1, "same source episode cannot be assigned twice using different target numbers");
        duplicateSource[third.Path] = new(key, doc.Episodes.First(), 0, 2);
        check(EpisodeMatching.Conflicts(duplicateSource).Count == 1, "duplicate target episode numbers are detected across different source episodes");
        var state = new LibraryStateStore { Documents = media.Documents, EpisodeBindings = media.EpisodeBindings, ExpandedRows = new() { folder, folder + "|season:0" } };
        var restored = JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(state))!;
        check(restored.EpisodeBindings[file.Path].Episode.Id == "sp" && restored.Documents[key].Episodes.Count == 4 && restored.ExpandedRows.Count == 2, "episode bindings, provider documents and tree expansion survive serialization");
        var output = new LibraryOutputService(new ImageDownloadService(new HttpClient(new WorkflowHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 255, 216, 255, 217 }) }))));
        var scope = new LibraryMedia { Folder = folder, Title = "测试剧集", Kind = LibraryMediaKind.Series, Files = new() { file }, Matches = media.Matches, Documents = media.Documents, EpisodeBindings = media.EpisodeBindings };
        await output.ScrapeAsync(scope, MediaOutputProfile.Jellyfin, false, true);
        var nfo = XDocument.Load(Path.ChangeExtension(file.Path, ".nfo")).Root!;
        check(nfo.Element("title")?.Value == "特别篇" && nfo.Element("season")?.Value == "0" && nfo.Element("episode")?.Value == "2" && nfo.Elements("uniqueid").Any(e => e.Value == "sp"), "single-episode NFO uses reviewed target numbers and original provider episode ID");
        check(!File.Exists(Path.Combine(folder, "tvshow.nfo")) && !File.Exists(Path.Combine(folder, "season.nfo")) && !File.Exists(Path.Combine(folder, "S01E01.nfo")), "single-episode scraping does not write show, season or neighbouring episode files");
        var rename = new LibraryRenameService(); var preview = rename.BuildPreview(scope, LibraryRenameService.EpisodeTemplate);
        check(preview.Single().ProposedName.Contains("S00E02 - 特别篇") && preview.Single().RelatedFiles.Count == 1, "rename preview uses the same reviewed SP binding and displays its NFO sidecar");
        rename.Execute(preview.SelectMany(g => g.Operations).ToList());
        file.Path = preview.Single().Operations.Single(p => p.Source == file.Path).Destination; scope.EpisodeBindings = new() { [file.Path] = new(key, doc.Episodes.Last(), 0, 2) };
        check(rename.BuildPreview(scope, LibraryRenameService.EpisodeTemplate).Single().RelatedFiles.Count == 1, "unchanged video previews still display associated NFO files");
        var provider = new BangumiMetadataProvider(new HttpClient(new WorkflowHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("episodes") ? request.RequestUri.Query.Contains("type=1") ? "{\"data\":[{\"id\":99,\"type\":1,\"ep\":11.5,\"name_cn\":\"番外\"},{\"id\":100,\"type\":2,\"ep\":1,\"name\":\"OP\"}]}" : "{\"data\":[{\"id\":1,\"type\":0,\"ep\":1,\"name\":\"regular\"}]}" : "{\"id\":123,\"name_cn\":\"测试\",\"images\":{}}", Encoding.UTF8, "application/json") })));
        var specialDoc = await provider.LoadAsync("123", LibraryMediaKind.Series, new[] { 2 });
        check(specialDoc.Episodes.Count == 2 && specialDoc.Episodes.Single(e => e.Season == 0).Id == "99", "Bangumi type-1 decimal specials remain available for manual S00 mapping while OP stays excluded");
    }
}
