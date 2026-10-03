using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class MetadataEditingWorkflowTests
{
    public static async Task Run(string temporary, Action<bool, string> check)
    {
        check(new AppConfig().DefaultMetadataSources.SequenceEqual(new[] { "TMDB" }), "new configurations default exclusively to TMDB");
        var requests = 0;
        var api = new TmdbApiClient("test", "zh-CN", new HttpClient(new WorkflowHandler(request =>
        {
            requests++;
            var json = request.RequestUri!.AbsolutePath.Contains("/season/") ? """
                {"season_number":3,"name":"Third season","overview":"Season story","vote_average":8.2,"episodes":[
                 {"id":101,"episode_number":1,"season_number":3,"name":"First","overview":"First story","air_date":"2026-07-01","vote_average":9.1,"vote_count":91,"runtime":24},
                 {"id":102,"episode_number":2,"season_number":3,"name":"Second","overview":"Second story","air_date":"2026-07-08","vote_average":6.7,"vote_count":67,"runtime":24}]}
                """ : """
                {"id":100,"name":"Source show","original_name":"Original show","overview":"Show story","first_air_date":"2026-07-01","vote_average":7.7,"seasons":[{"season_number":3}]}
                """;
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        })));
        var source = await new TmdbMetadataProvider(api).LoadAsync("100", LibraryMediaKind.Series, Array.Empty<int>());
        check(source.Episodes[0].Rating == 9.1 && source.Episodes[1].Rating == 6.7 && source.Episodes[0].VoteCount == 91 && source.Seasons[0].Rating == 8.2, "TMDB adapter retains individual episode scores, votes and independent season score");
        var folder = Path.Combine(temporary, "metadata-editor"); var seasonFolder = Path.Combine(folder, "Season 03"); Directory.CreateDirectory(seasonFolder);
        foreach (var name in new[] { "S03E01.mkv", "S03E02.mkv" }) File.WriteAllText(Path.Combine(seasonFolder, name), "isolated video");
        var media = new LibraryScanner().Scan(folder, LibraryMediaKind.Series, false).Single(); media.Title = "Source show";
        media.Matches[3] = new(new("TMDB", "100", source.Title, source.OriginalTitle, 2026, source.Overview, ""), source);
        var key = EpisodeMatching.Key(source, media.Kind); media.Documents[key] = source;
        var first = media.Files.Single(f => f.Episode == 1); var second = media.Files.Single(f => f.Episode == 2);
        media.EpisodeBindings[first.Path] = new(key, source.Episodes[0] with { Rating = null, VoteCount = 0 }, 3, 1);
        check(MetadataEditing.Episode(media, first).Rating == 9.1 && MetadataEditing.Episode(media, second).Rating == 6.7 && MetadataEditing.Season(media, 3).Rating == 8.2, "old binding uses newly loaded episode score and never substitutes the show score");

        var work = MetadataEditing.Copy(MetadataEditing.Work(media)); work.Title = "Edited show"; work.OriginalTitle = "原始名称"; work.Overview = "自定义作品简介"; work.Rating = 8.8; work.Genres = new() { "Animation" }; work.Cast = new() { new() { Name = "Local actor", Character = "Role" } };
        MetadataEditing.Save(media, work); work.Cast[0].Name = "Changed draft after save";
        check(media.Metadata!.Title == "Edited show" && media.Metadata.Cast[0].Name == "Local actor" && source.Title == "Source show" && source.Cast.Count == 0, "saved work edits detach nested collections and leave provider cache intact");
        var season = MetadataEditing.Copy(MetadataEditing.Season(media, 3)); season.Title = "自定义第三季"; season.Overview = "季度简介"; season.Rating = 8.4; MetadataEditing.Save(media, season, 3);
        var episode = MetadataEditing.Copy(MetadataEditing.Episode(media, first)); episode.Title = "自定义第一集"; episode.OriginalTitle = "First original"; episode.Overview = "人工校正译文"; episode.OriginalOverview = "Original episode description"; episode.Rating = 9.5; episode.VoteCount = 120; episode.Date = "2026-07-02"; episode.Cast.Clear(); episode.Genres.Clear(); episode.Studios.Clear(); MetadataEditing.Save(media, episode, 3, first);
        check(EpisodeMatching.Resolve(media, first)!.Episode.Title == "自定义第一集" && MetadataEditing.Episode(media, second).Title == "Second" && source.Episodes[0].Title == "First", "episode edits affect selected file while sibling and source data remain unchanged");
        check(MetadataEditing.Season(media, 3).Title == "自定义第三季" && media.Metadata.Title == "Edited show", "season and work edits remain independent");
        var nfo = Path.ChangeExtension(first.Path, ".nfo"); File.WriteAllText(nfo, "<episodedetails><title>Old</title><actor><name>Remove me</name></actor><genre>Old genre</genre><custom>Keep me</custom><fileinfo><streamdetails /></fileinfo></episodedetails>");
        var networkCalls = 0;
        var disconnected = new HttpClient(new WorkflowHandler(_ => { networkCalls++; throw new Exception("Metadata-only update must not access a provider or artwork network."); }));
        var output = new LibraryOutputService(new ImageDownloadService(disconnected), new TmdbApiClient("test", "zh-CN", disconnected), new BangumiMetadataProvider(disconnected));
        var report = await output.UpdateMetadataAsync(media, MediaOutputProfile.Jellyfin);
        check(report.Written == 4 && report.Images == 0 && networkCalls == 0, "update metadata writes show, season and episodes using current data with zero network calls");
        var firstNfo = XDocument.Load(nfo).Root!; var secondNfo = XDocument.Load(Path.ChangeExtension(second.Path, ".nfo")).Root!;
        check(firstNfo.Element("title")!.Value == "自定义第一集" && firstNfo.Element("plot")!.Value == "人工校正译文" && firstNfo.Element("rating")!.Value == "9.5" && firstNfo.Element("ratings")!.Element("rating")!.Element("votes")!.Value == "120" && secondNfo.Element("rating")!.Value == "6.7", "each episode exports its own rating and edited translated synopsis");
        check(firstNfo.Element("custom")!.Value == "Keep me" && firstNfo.Element("fileinfo") != null && !firstNfo.Elements("actor").Any() && !firstNfo.Elements("genre").Any(), "explicitly cleared editable collections disappear while unrelated custom NFO and fileinfo fields survive");
        check(XDocument.Load(Path.Combine(folder, "tvshow.nfo")).Root!.Element("title")!.Value == "Edited show" && XDocument.Load(Path.Combine(seasonFolder, "season.nfo")).Root!.Element("title")!.Value == "自定义第三季", "work and season NFOs use their separately edited titles");
        check(Directory.GetFiles(folder, "*.bak", SearchOption.AllDirectories).Length == 0 && File.ReadAllText(first.Path) == "isolated video", "NFO updates create no backup sidecars and leave video data untouched");
        var scanned = new LibraryScanner().Scan(folder, LibraryMediaKind.Series, false).Single();
        check(scanned.LocalMetadata!.Rating == 8.8 && scanned.LocalSeasons[3].Rating == 8.4 && scanned.Files.Single(f => f.Episode == 1).LocalMetadata!.Rating == 9.5, "scanner reads actual work, season and episode NFO ratings independently");
        var state = new LibraryStateStore { Matches = new() { [folder] = media.Matches }, Documents = media.Documents, EpisodeBindings = media.EpisodeBindings, MetadataEdits = new() { [folder] = media.Edits } };
        var restored = JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(state))!; LibrarySnapshotStore.ApplyRecords(new[] { scanned }, restored);
        check(scanned.Metadata!.Title == "Edited show" && MetadataEditing.Season(scanned, 3).Title == "自定义第三季" && MetadataEditing.Episode(scanned, scanned.Files.Single(f => f.Episode == 1)).OriginalOverview == "Original episode description", "all three editing scopes and original descriptions survive a persisted reload");
        var oldShow = File.ReadAllBytes(Path.Combine(folder, "tvshow.nfo")); var oldSecond = File.ReadAllBytes(Path.ChangeExtension(second.Path, ".nfo")); var oldSeason = File.ReadAllBytes(Path.Combine(seasonFolder, "season.nfo"));
        var scope = new LibraryMedia { Folder = folder, Title = media.Title, Kind = media.Kind, Files = new() { first }, Matches = media.Matches, Documents = media.Documents, EpisodeBindings = media.EpisodeBindings, Edits = media.Edits };
        report = await output.UpdateMetadataAsync(scope, MediaOutputProfile.Emby, episodesOnly: true);
        check(report.Written == 1 && oldShow.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "tvshow.nfo"))) && oldSecond.SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(second.Path, ".nfo"))) && oldSeason.SequenceEqual(File.ReadAllBytes(Path.Combine(seasonFolder, "season.nfo"))), "single-episode update preserves siblings and show/season NFO bytes");
        var rename = new LibraryRenameService(); var plan = rename.Preview(scope, LibraryRenameService.EpisodeTemplate);
        check(plan.Any(p => p.Destination.Contains("Edited show - S03E01 - 自定义第一集")), "rename preview uses edited work and episode titles");
        MetadataEditing.MoveFileEdits(media.Edits, plan);
        var destination = plan.Single(p => p.Source == first.Path).Destination;
        check(media.Edits.Episodes.ContainsKey(destination) && !media.Edits.Episodes.ContainsKey(first.Path), "file metadata edits follow successfully renamed paths");
        var invalid = MetadataEditing.Copy(source); invalid.Rating = double.NaN;
        var rejected = false; try { MetadataEditing.Save(media, invalid); } catch (ArgumentException) { rejected = true; }
        check(rejected && media.Metadata.Title == "Edited show", "invalid edit validation cannot replace previous saved metadata");
        var manualFolder = Path.Combine(temporary, "unmatched-manual"); Directory.CreateDirectory(manualFolder); var manualVideo = Path.Combine(manualFolder, "S01E01.mkv"); File.WriteAllText(manualVideo, "video");
        var manual = new LibraryScanner().Scan(manualFolder, LibraryMediaKind.Series, false).Single();
        MetadataEditing.Save(manual, new() { Title = "Manual show", Overview = "Local story" }); MetadataEditing.Save(manual, new() { Title = "Manual episode", Rating = 7.1 }, file: manual.Files[0]);
        report = await output.UpdateMetadataAsync(manual, MediaOutputProfile.Jellyfin);
        check(report.Written == 3 && XDocument.Load(Path.ChangeExtension(manualVideo, ".nfo")).Root!.Element("title")!.Value == "Manual episode" && networkCalls == 0, "custom metadata can export an unmatched show and episode without a remote ID or TMDB key");
    }
}
