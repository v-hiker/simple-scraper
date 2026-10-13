using SimpleScraper.Models;
using SimpleScraper.Services;
using System.Text.Json;

static class RecognitionWorkflowTests
{
    public static void Run(string temporary, Action<bool, string> check)
    {
        var scanner = new LibraryScanner();
        string Folder(string name) { var p = Path.Combine(temporary, "recognition", name); Directory.CreateDirectory(p); return p; }
        LibraryMedia Scan(string name, params string[] filenames)
        {
            var folder = Folder(name);
            foreach (var file in filenames) File.WriteAllText(Path.Combine(folder, file), "isolated placeholder");
            return scanner.Scan(folder, LibraryMediaKind.Auto, false).Single();
        }
        foreach (var (name, s, e) in new (string, int, int)[] {
            ("Show.S03E01.mkv",3,1), ("Show_S03_E02.mkv",3,2), ("Show.s3.ep3.mp4",3,3),
            ("Show 3x04.mkv",3,4), ("Show Season 3 Episode 5.mkv",3,5),
            ("节目第三季第06集.mkv",3,6), ("节目第7集.mkv",1,7), ("节目 第08話.mkv",1,8),
            ("Show EP09.mkv",1,9), ("Show Episode 10.mkv",1,10), ("E11.mkv",1,11),
            ("Show.S03E01.1080p.mkv",3,1), ("Show.S03E01-2160p.mkv",3,1),
            ("Ｓ０３Ｅ１２.mkv",3,12) })
        {
            var m = Scan("explicit-" + e + "-" + name.GetHashCode(), name);
            var f = m.Files.Single();
            check(m.Kind == LibraryMediaKind.Series && f.Season == s && f.Episode == e && !f.NeedsReview, "explicit episode recognized: " + name);
        }
        foreach (var names in new[] {
            new[]{"[Ygm] Grand Blue [01][Ma10p_2160p][x265_aac_srt].mkv","[Ygm] Grand Blue [02][Ma10p_2160p][x265_aac_srt].mkv"},
            new[]{"[SubsPlease] Show - 01 (1080p) [ABCD].mkv","[SubsPlease] Show - 02 (1080p) [EFAB].mkv"},
            new[]{"Show [01v2][1080p].mkv","Show [02][1080p].mkv"},
            new[]{"Show 1.01.mkv","Show 1.02.mkv"} })
        {
            var m=Scan("weak-"+names[0].GetHashCode(),names);
            check(m.Kind==LibraryMediaKind.Series && m.Files.Select(f=>f.Episode).Order().SequenceEqual(new[]{1,2}), "corroborating episode sequence: " + names[0]);
        }
        foreach(var (folder,number) in new (string,int)[]{("第三季",3),("第3季",3),("Season_03",3),("Season.03",3),("S03",3),("第十二季",12),("Season 00",0),("Specials",0),("特别篇",0)})
        {
            var m=Scan(folder,"01.mkv","02.mkv");
            check(m.Kind==LibraryMediaKind.Series && m.Files.All(f=>f.Season==number) && m.Files.Select(f=>f.Episode).Order().SequenceEqual(new[]{1,2}),"season directory hints: "+folder);
        }
        foreach(var name in new[]{"Film (2026) 2160p.mkv","Film [2026][1080p].mkv","Film CD1.mkv","Film Part 1.mkv","Film - 2026.mkv","Film [01].mkv","1080p.mkv","Unknown.mkv"})
            check(Scan("movie-"+name.GetHashCode(),name).Kind==LibraryMediaKind.Movie,"ambiguous movie is not forced into TV: "+name);
        var parts=Scan("movie-parts","Film CD1.mkv","Film CD2.mkv","Film Part 1.mkv","Film Part 2.mkv");
        check(parts.Kind==LibraryMediaKind.Movie,"multipart movie does not supply episode evidence");
        foreach(var name in new[]{"S01E11.5.mkv","S01E01E02.mkv","Show 1x01-02.mkv","Show EP01&02.mkv","[Ygm] Show [11.5].mkv","[Group] Show - 01-02 [1080p].mkv"})
        {
            var folder=Folder("review-"+name.GetHashCode());File.WriteAllText(Path.Combine(folder,name),"fixture");
            var file=scanner.Scan(folder,LibraryMediaKind.Series,false).Single().Files.Single();
            check(file.NeedsReview&&file.Episode==0&&file.SourceNumber.Length>0,"fractional/multiple episode preserved for review: "+name);
        }
        var extra=Scan("extras","Show S01E01.mkv","[Ygm] Show [NCOP01][1080p].mkv","Show ED02.mkv","Show PV01.mkv","Show OVA01.mkv");
        check(extra.Files.Count(f=>f.Exclusion.Length>0&&!f.NeedsReview)==3&&extra.Files.Single(f=>f.Name.Contains("OVA01")).Season==0,"OP ED PV excluded while numbered OVA is a special");
        var nested=Folder("nested-series");Directory.CreateDirectory(Path.Combine(nested,"Video"));File.WriteAllText(Path.Combine(nested,"Video","Show EP01.mkv"),"fixture");
        check(scanner.Scan(nested,LibraryMediaKind.Auto,false).Single().Kind==LibraryMediaKind.Series,"episode names in a video subdirectory identify TV");
        check(scanner.Scan(nested,LibraryMediaKind.Movie,false,overrides:new Dictionary<string,LibraryMediaKind>{{nested,LibraryMediaKind.Series}}).Single().Files.Single().Episode==1,"explicit refresh applies manual media type before discovering subdirectory files");
        var nfo=Folder("nfo-only");File.WriteAllText(Path.Combine(nfo,"Unknown.mkv"),"fixture");File.WriteAllText(Path.Combine(nfo,"Unknown.nfo"),"<episodedetails><season>3</season><episode>7</episode></episodedetails>");
        var nfoMedia=scanner.Scan(nfo,LibraryMediaKind.Auto,false).Single();
        check(nfoMedia.Kind==LibraryMediaKind.Series&&nfoMedia.Files.Single().Season==3&&nfoMedia.Files.Single().Episode==7&&!nfoMedia.Files.Single().NeedsReview,"episode NFO is a fallback for an unnumbered episode");
        File.WriteAllText(Path.Combine(nfo,"S01E11.5.mkv"),"fixture");File.WriteAllText(Path.Combine(nfo,"S01E11.5.nfo"),"<episodedetails><season>1</season><episode>11</episode></episodedetails>");
        check(scanner.Scan(nfo,LibraryMediaKind.Auto,false).Single().Files.Single(f=>f.Name=="S01E11.5.mkv").NeedsReview,"integer NFO never truncates a fractional filename");
        var movieNfo=Scan("movie-nfo","Film S01E01.mkv");File.WriteAllText(Path.Combine(movieNfo.Folder,"movie.nfo"),"<movie><title>Film</title></movie>");
        check(scanner.Scan(movieNfo.Folder,LibraryMediaKind.Auto,false).Single().Kind==LibraryMediaKind.Movie,"explicit movie NFO takes precedence over filename hints");
        File.WriteAllText(Path.Combine(movieNfo.Folder,"tvshow.nfo"),"<tvshow><title>Show</title></tvshow>");
        check(scanner.Scan(movieNfo.Folder,LibraryMediaKind.Auto,false).Single().Kind==LibraryMediaKind.Series,"tvshow NFO identifies TV even when another NFO conflicts");
        var corrupt=Scan("bad-nfo","Film (2026).mkv");File.WriteAllText(Path.Combine(corrupt.Folder,"movie.nfo"),"<broken>");
        check(scanner.Scan(corrupt.Folder,LibraryMediaKind.Auto,false).Single().ScanWarning.Length>0,"malformed NFO is preserved and reports a warning");
        var auto=Scan("第三季","[Ygm] Grand Blue [01][Ma10p_2160p].mkv","[Ygm] Grand Blue [02][Ma10p_2160p].mkv");
        var old=scanner.Scan(auto.Folder,LibraryMediaKind.Movie,false).Single();var snapshot=new LibrarySnapshot(1,DateTimeOffset.UtcNow,new(){old});
        var bytes=JsonSerializer.Serialize(snapshot);var offline=auto.Folder+"-offline";Directory.Move(auto.Folder,offline);
        var corrected=LibrarySnapshotStore.CopyMedia(snapshot,LibraryMediaKind.Auto).Single();
        check(corrected.Kind==LibraryMediaKind.Series&&corrected.Files.Where(f=>f.Name.Contains("Grand Blue")).All(f=>f.Season==3)&&JsonSerializer.Serialize(snapshot)==bytes,"old cached movie becomes TV from saved filenames while offline; raw cache stays immutable");
        check(LibrarySnapshotStore.CopyMedia(snapshot,LibraryMediaKind.Movie).Single().Kind==LibraryMediaKind.Movie,"explicit movie library is not automatically converted");
        var pending=new LibraryMedia { Folder=auto.Folder, Kind=LibraryMediaKind.Series, Files=new(){new(){Path=Path.Combine(auto.Folder,"[Ygm] Grand Blue [01][1080p].mkv"),NeedsReview=true,Exclusion="old numbering not recognized"}}};
        var pendingCopy=LibrarySnapshotStore.CopyMedia(new(1,DateTimeOffset.UtcNow,new(){pending}),LibraryMediaKind.Series).Single().Files.Single();
        check(!pendingCopy.NeedsReview&&pendingCopy.Season==3&&pendingCopy.Episode==1&&pendingCopy.Exclusion.Length==0,"existing TV cache gains new filename parsing while offline");
        var state=new LibraryStateStore{MediaKinds=new(){[auto.Folder]=LibraryMediaKind.Movie}};
        LibrarySnapshotStore.ApplyRecords(new[]{corrected},state);
        check(corrected.Kind==LibraryMediaKind.Movie,"manual media type overrides automatic classification");
        state.MediaKinds[auto.Folder]=LibraryMediaKind.Series;var restored=JsonSerializer.Deserialize<LibraryStateStore>(JsonSerializer.Serialize(state))!;
        var forced=LibrarySnapshotStore.CopyMedia(snapshot).Single();LibrarySnapshotStore.ApplyRecords(new[]{forced},restored);
        check(forced.Kind==LibraryMediaKind.Series&&forced.Files.Where(f=>f.Name.Contains("Grand Blue")).All(f=>f.Season==3),"manual TV type persists and reparses file numbering without filesystem reads");
        Directory.Move(offline,auto.Folder);
    }
}
