using System.Xml.Linq;
using SimpleScraper.Models;
using SimpleScraper.Services;

static class ExtrasWorkflowTests
{
    public static void Run(string temporary, Action<bool,string> check)
    {
        var scanner=new LibraryScanner();var rename=new LibraryRenameService();
        foreach(var profile in new[]{MediaOutputProfile.Jellyfin,MediaOutputProfile.Emby})
        {
            var root=Path.Combine(temporary,"extras-"+profile.Name,"Animation (2026)");var oped=Path.Combine(root,"OPED");var season=Path.Combine(root,"Season 03");var menu=Path.Combine(season,"Menu");
            Directory.CreateDirectory(oped);Directory.CreateDirectory(menu);
            string Write(string directory,string name,string value){var p=Path.Combine(directory,name);File.WriteAllText(p,value);return p;}
            var op=Write(oped,"[Ygm] Show [NCOP01][1080p].mkv","opening bytes");
            var ed=Write(oped,"[Ygm] Show [NCED01][1080p].mkv","ending bytes");
            var menus=Write(menu,"Menu01.mkv","menu bytes");
            var regular=Write(season,"S03E01.mkv","regular bytes");
            var pending=Write(season,"S03E11.5.mkv","fractional bytes");
            var subtitle=Write(oped,Path.GetFileNameWithoutExtension(op)+".zh-CN.ass","subtitle bytes");
            var thumb=Write(oped,Path.GetFileNameWithoutExtension(op)+"-thumb.jpg","thumb bytes");
            var nfo=Write(oped,Path.GetFileNameWithoutExtension(op)+".nfo","<movie><title>Opening custom title</title><thumb>"+Path.GetFileName(thumb)+"</thumb></movie>");
            var media=scanner.Scan(root,LibraryMediaKind.Series,false).Single();
            check(media.Files.Count==5&&media.Files.Count(f=>f.Extra!=null)==3&&media.Files.Single(f=>f.Path==menus).Season==3,"scanner discovers root OPED and nested season Menu for "+profile.Name);
            var groups=rename.BuildPreview(media,LibraryRenameService.EpisodeTemplate,profile);
            var changes=groups.Where(g=>g.Selected).ToList();
            check(changes.Count==3&&changes.All(g=>g.IsExtra)&&groups.Single(g=>g.File.Path==regular).State==RenamePreviewState.Unmatched&&groups.Single(g=>g.File.Path==pending).State==RenamePreviewState.Excluded,"unmatched OP ED Menu organize independently; regular and fractional episodes stay protected: "+profile.Name);
            var opening=changes.Single(g=>g.File.Path==op);var target=opening.Operations.First(p=>p.Source==op).Destination;
            check(target==Path.Combine(root,"extras","Animation - NCOP01.mkv")&&opening.Operations.Count==4,"extra keeps readable type and carries NFO subtitle and thumbnail: "+profile.Name);
            check(changes.Single(g=>g.File.Path==menus).Operations.Single().Destination==Path.Combine(season,"extras","Animation - S03 - Menu01.mkv"),"season extra stays in its season extras directory: "+profile.Name);
            check(!Directory.Exists(Path.Combine(root,"extras"))&&!Directory.Exists(Path.Combine(season,"extras")),"extra preview is read-only: "+profile.Name);
            var plan=changes.SelectMany(g=>g.Operations).ToList();var content=plan.Where(p=>Path.GetExtension(p.Source)!=".nfo").ToDictionary(p=>p.Source,p=>File.ReadAllBytes(p.Source));
            rename.Execute(plan);
            check(plan.Where(p=>content.ContainsKey(p.Source)).All(p=>File.ReadAllBytes(p.Destination).SequenceEqual(content[p.Source]))&&File.ReadAllText(regular)=="regular bytes"&&File.ReadAllText(pending)=="fractional bytes","extra moves preserve bytes and neighboring episodes: "+profile.Name);
            var xml=XDocument.Load(opening.Operations.Single(p=>p.Source==nfo).Destination);
            check(xml.Root?.Element("title")?.Value=="Opening custom title"&&xml.Root.Element("season")==null&&xml.Root.Element("episode")==null&&xml.Root.Element("thumb")?.Value=="Animation - NCOP01-thumb.jpg","extra NFO preserves custom metadata, updates art and never invents S00 numbering: "+profile.Name);
            var rescanned=scanner.Scan(root,LibraryMediaKind.Series,false).Single();
            check(rescanned.Files.Count(f=>f.Extra!=null)==3&&rename.BuildPreview(rescanned,LibraryRenameService.EpisodeTemplate,profile).Count(g=>g.IsExtra&&g.State==RenamePreviewState.Unchanged)==3,"rescan discovers organized season extras and reorganization is idempotent: "+profile.Name);
            var mapped=rescanned.Files.Single(f=>f.Extra?.Tag=="NCOP01");var state=new LibraryStateStore{FileMappings=new(){[mapped.Path]=new(0,1,true,true)}};
            LibrarySnapshotStore.ApplyRecords(new[]{rescanned},state);
            check(rename.BuildPreview(rescanned,LibraryRenameService.EpisodeTemplate,profile).Single(g=>g.File.Path==mapped.Path).State==RenamePreviewState.Excluded,"explicitly excluded extras are protected: "+profile.Name);
        }
        foreach(var name in new[]{"OP01.mkv","ED2.mp4","[Group] Show [NCOP03][2160p].mkv","Show - Menu04.mkv","PV01.mkv","CM01.mkv","Show NCED01v2.mkv"})
            check(ExtraMedia.Identify(temporary,Path.Combine(temporary,name))!=null,"extra type identified: "+name);
        foreach(var name in new[]{"OPEN.mkv","EDEN.mkv","S01E01.mkv","S01E11.5.mkv","SP01.mkv","OVA01.mkv","OAD01.mkv","Menu.png"})
            check(!LibraryScanner.IsVideo(name)||ExtraMedia.Identify(temporary,Path.Combine(temporary,name))==null,"normal/special or non-video is not an OP ED Menu extra: "+name);
        var movie=Path.Combine(temporary,"movie-with-extras");Directory.CreateDirectory(Path.Combine(movie,"extras"));File.WriteAllText(Path.Combine(movie,"Film.mkv"),"movie");File.WriteAllText(Path.Combine(movie,"extras","S01E01.mkv"),"extra");
        var movieMedia=scanner.Scan(movie,LibraryMediaKind.Auto,false).Single();
        check(movieMedia.Kind==LibraryMediaKind.Movie&&movieMedia.Files.Count==2&&movieMedia.Files.Count(f=>f.Extra!=null)==1,"movie extras are discovered and do not classify a movie as TV");
        check(LibrarySnapshotStore.CopyMedia(new(1,DateTimeOffset.UtcNow,new(){movieMedia}),LibraryMediaKind.Auto).Single().Kind==LibraryMediaKind.Movie,"cached extras do not promote a movie into TV");
        var conflict=Path.Combine(temporary,"extras-collision");Directory.CreateDirectory(conflict);File.WriteAllText(Path.Combine(conflict,"OP1.mkv"),"first");File.WriteAllText(Path.Combine(conflict,"OP01.mkv"),"second");
        var conflictMedia=scanner.Scan(conflict,LibraryMediaKind.Series,false).Single();
        check(rename.BuildPreview(conflictMedia,LibraryRenameService.EpisodeTemplate).All(g=>g.State==RenamePreviewState.Conflict&&!g.Selected),"canonical extra-name collisions are blocked instead of silently numbered or overwritten");
        var legacy=conflictMedia.Files.Single(f=>f.Name=="OP1.mkv");var legacyMedia=new LibraryMedia{Folder=conflict,Title="Show",Kind=LibraryMediaKind.Series,Files=new(){legacy}};
        LibrarySnapshotStore.ApplyRecords(new[]{legacyMedia},new(){FileMappings=new(){[legacy.Path]=new(0,0,true,true)}});
        check(rename.BuildPreview(legacyMedia,LibraryRenameService.EpisodeTemplate).Single().IsExtra&&rename.BuildPreview(legacyMedia,LibraryRenameService.EpisodeTemplate).Single().Selected,"legacy 0/0 episode skips still permit OP ED extras organization");
        var rollback=Path.Combine(temporary,"extras-rollback");Directory.CreateDirectory(rollback);File.WriteAllText(Path.Combine(rollback,"OP01.mkv"),"rollback video");File.WriteAllText(Path.Combine(rollback,"OP01.nfo"),"<broken>");
        var rollbackMedia=scanner.Scan(rollback,LibraryMediaKind.Series,false).Single();var rollbackPlan=rename.Preview(rollbackMedia,LibraryRenameService.EpisodeTemplate);var failed=false;
        try{rename.Execute(rollbackPlan);}catch(AggregateException){failed=true;}
        check(failed&&File.ReadAllText(Path.Combine(rollback,"OP01.mkv"))=="rollback video"&&File.ReadAllText(Path.Combine(rollback,"OP01.nfo"))=="<broken>"&&!Directory.Exists(Path.Combine(rollback,"extras")),"extra move failure restores original files and removes newly created empty folders");
        var bound=new LocalMediaFile{Path=Path.Combine(rollback,"OP01.mkv"),Extra=new("OP01"),Season=0,Episode=1};var doc=new MetadataDocument{Provider="Bangumi",Id="1",Title="Show",Episodes=new(){new("sp",0,1,"Official opening","","",0,"")}};
        var boundMedia=new LibraryMedia{Folder=rollback,Title="Show",Kind=LibraryMediaKind.Series,Files=new(){bound},Matches=new(){[0]=new(new("Bangumi","1","Show","",0,"",""),doc)}};
        check(rename.BuildPreview(boundMedia,LibraryRenameService.EpisodeTemplate).Single().IsExtra==false,"a confirmed source special keeps its S00 episode workflow even if filename says OP");
        var outside=new LibraryMedia{Folder=rollback,Title="Show",Kind=LibraryMediaKind.Series,Files=new(){new(){Path=Path.Combine(movie,"Film.mkv"),Extra=new("OP01")}}};failed=false;
        try{rename.Preview(outside,LibraryRenameService.EpisodeTemplate);}catch(IOException){failed=true;}
        check(failed,"extra organization cannot move a source outside the selected media folder");
    }
}
