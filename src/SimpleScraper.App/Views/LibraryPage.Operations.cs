using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using System.Xml.Linq;

namespace SimpleScraper.Views;

public sealed partial class LibraryPage
{
    private async Task MapFile(LocalMediaFile file)
    {
        var season = new NumberBox { Header = L.Text("Season"), Value = file.Season, Minimum = 0, Maximum = 999 }; var episode = new NumberBox { Header = L.Text("Episode"), Value = Math.Max(1, file.Episode), Minimum = 1, Maximum = 9999 }; var skip = new CheckBox { Content = L.Text("Exclude this file"), IsChecked = file.Exclusion.Length > 0 && !file.NeedsReview }; var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(Text(file.Name)); if (file.NeedsReview) panel.Children.Add(Text("This file needs a source episode match. For specials, use Search and match and select the corresponding SP in the dropdown.")); panel.Children.Add(season); panel.Children.Add(episode); panel.Children.Add(skip);
        Id(season, "EditEpisodeSeason"); Id(episode, "EditEpisodeNumber"); Id(skip, "EditEpisodeExclude");
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Set season / episode"), Content = panel, PrimaryButtonText = L.Text("Apply"), CloseButtonText = L.Text("Cancel") }; if (await Ui.Show(dialog) != ContentDialogResult.Primary) return;
        if (!double.IsFinite(season.Value) || !double.IsFinite(episode.Value) || season.Value != Math.Truncate(season.Value) || episode.Value != Math.Truncate(episode.Value) || season.Value < 0 || season.Value > 999 || episode.Value < 1 || episode.Value > 9999) throw new ArgumentException(L.Text("Use integer season and episode numbers.")); file.Season = (int)season.Value; file.Episode = (int)episode.Value; file.Exclusion = skip.IsChecked == true ? L.Text("Manually excluded") : "";
        file.NeedsReview = false; file.ManuallyExcluded = skip.IsChecked == true; _store.FileMappings = new(_store.FileMappings) { [file.Path] = new(file.Season, file.Episode, skip.IsChecked == true, skip.IsChecked == true) }; var bindings = new Dictionary<string, EpisodeBinding>(_store.EpisodeBindings); bindings.Remove(file.Path); _store.EpisodeBindings = bindings; Current?.EpisodeBindings.Remove(file.Path); await Save(); await ShowRows();
    }

    private Task Search() => Search(false);

    private async Task SetMediaType(LibraryMedia media)
    {
        var type = new ComboBox { Header = L.Text("Media type"), ItemsSource = new[] { L.Text("Movies"), L.Text("TV series") }, SelectedIndex = media.Kind == LibraryMediaKind.Series ? 1 : 0 }; Id(type, "MediaTypeChoice");
        var panel = new StackPanel { Spacing = 12, Width = 530 }; panel.Children.Add(Text(media.Metadata?.Title ?? media.Title, 18)); panel.Children.Add(Text(media.Folder)); panel.Children.Add(type); panel.Children.Add(Text("Changing the media type reads this folder again and preserves matching records. It does not modify media files."));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Set media type"), Content = panel, PrimaryButtonText = L.Text("Apply"), CloseButtonText = L.Text("Cancel"), DefaultButton = ContentDialogButton.Primary }; dialog.Resources["ContentDialogMaxWidth"] = 620d;
        if (await Ui.Show(dialog) != ContentDialogResult.Primary) return;
        var kind = type.SelectedIndex == 1 ? LibraryMediaKind.Series : LibraryMediaKind.Movie;
        if (kind == media.Kind && _store.MediaKinds.GetValueOrDefault(media.Folder, LibraryMediaKind.Auto) == kind) return;
        // Read first so a disconnected directory cannot leave a partial edit.
        var raw = await Task.Run(() => _scanner.Scan(media.Folder, kind, false).Single());
        _store.MediaKinds = new(_store.MediaKinds) { [media.Folder] = kind }; await Save();
        foreach (var library in _store.Libraries)
        {
            var key = LibrarySnapshotStore.Key(library); var snapshot = _snapshots.GetValueOrDefault(key) ?? await Task.Run(() => _snapshotStore.Load(library));
            if (snapshot == null || !snapshot.Media.Any(m => m.Folder.Equals(raw.Folder, StringComparison.OrdinalIgnoreCase))) continue;
            var updated = snapshot with { Media = snapshot.Media.Select(m => m.Folder.Equals(raw.Folder, StringComparison.OrdinalIgnoreCase) ? raw : m).ToList() };
            await Task.Run(() => _snapshotStore.Save(library, updated)); _snapshots[key] = updated;
        }
        await LoadLibrary();
    }

    private async Task Search(bool openComparison)
    {
        if (Current is not { } media) return;
        var focusFile = Selected?.File?.Path; var focusSeason = Selected?.Season >= 0 ? Selected.Season : null;
        IReadOnlyList<string>? sources = null; string? title = null; var skipInitialSearch = false;
        SearchMatchDialog dialog;
        while (true)
        {
            dialog = new SearchMatchDialog(media, _config, focusFile, openComparison, focusSeason, sources, title, skipInitialSearch, _services.MetadataProvider) { XamlRoot = XamlRoot };
            var result = await Ui.Show(dialog);
            if (dialog.NeedsTmdbConfiguration)
            {
                sources = dialog.SelectedSources; title = dialog.SearchTitle; openComparison = false;
                skipInitialSearch = !await TmdbKeyPrompt.Configure(_config, XamlRoot);
                continue;
            }
            if (result != ContentDialogResult.Primary) { _status.Text = L.Text("Match cancelled."); return; }
            break;
        }
        if (dialog.Comparison is { } draft)
        {
            media.Matches = new(draft.Matches); media.Documents = new(draft.Documents); media.EpisodeBindings = new(draft.Bindings);
            var documents = new Dictionary<string, MetadataDocument>(_store.Documents); foreach (var d in draft.Documents) documents[d.Key] = d.Value; _store.Documents = documents;
            var bindings = new Dictionary<string, EpisodeBinding>(_store.EpisodeBindings); var mappings = new Dictionary<string, ManualFileMapping>(_store.FileMappings);
            foreach (var file in media.Files) { bindings.Remove(file.Path); if (draft.Bindings.TryGetValue(file.Path, out var b)) { bindings[file.Path] = b; file.Season = b.Season; file.Episode = b.Number; file.Exclusion = ""; file.NeedsReview = false; mappings[file.Path] = new(b.Season, b.Number, false); } else if (draft.Excluded.Contains(file.Path)) { file.Exclusion = file.Exclusion.Length > 0 ? file.Exclusion : L.Text("Manually excluded"); file.NeedsReview = false; mappings[file.Path] = new(file.Season, file.Episode, true, true); } else if (file.NeedsReview) mappings.Remove(file.Path); }
            _store.EpisodeBindings = bindings; _store.FileMappings = mappings;
        }
        else if (dialog.ConfirmedMatch is { } match) media.Matches = new(media.Matches) { [-1] = match };
        _store.Matches = new(_store.Matches) { [media.Folder] = new(media.Matches) }; await Save(); await ShowRows(); _tabs.SelectedIndex = 0; _status.Text = L.Text("Match saved. Ready to scrape or preview rename.");
    }

    private Task CompareEpisodes() => Search(true);

    private LibraryMedia Scope()
    {
        var selected = Selected!; var m = selected.Media; var files = selected.File is { } f ? new List<LocalMediaFile> { f } : selected.Season is int s ? m.Files.Where(f => s == -3 ? LibraryReview.IsExtra(m, f) : s == -2 ? f.NeedsReview : s < 0 ? f.Exclusion.Length > 0 && !f.NeedsReview && !LibraryReview.IsExtra(m, f) : f.Exclusion.Length == 0 && f.Season == s || f.Extra != null && (!f.ManuallyExcluded || f.Episode == 0) && ExtraMedia.SeasonHint(m.Folder, f.Path) == s).ToList() : m.Files;
        return new() { Folder = m.Folder, Title = m.Title, Kind = m.Kind, Year = m.Year, Files = files, Matches = m.Matches, Documents = m.Documents, EpisodeBindings = m.EpisodeBindings, Edits = m.Edits, LocalMetadata = m.LocalMetadata, LocalSeasons = m.LocalSeasons, SeasonNfoPaths = m.SeasonNfoPaths };
    }

    private async Task EditMetadata()
    {
        if (Selected is not { } selected || selected.File == null && selected.Season < 0) return;
        var document = selected.File != null ? MetadataEditing.Episode(selected.Media, selected.File) : selected.Season is int number ? MetadataEditing.Season(selected.Media, number) : MetadataEditing.Work(selected.Media);
        var dialog = new MetadataEditorDialog(document, _config) { XamlRoot = XamlRoot };
        if (await Ui.Show(dialog) != ContentDialogResult.Primary || dialog.Edited == null) return;
        MetadataEditing.Save(selected.Media, dialog.Edited, selected.Season, selected.File);
        _store.MetadataEdits[selected.Media.Folder] = selected.Media.Edits; await Save(); await ShowRows(true);
        _status.Text = L.Text("Metadata saved.");
    }

    private async Task UpdateMetadata()
    {
        if (Selected is not { } selected) return;
        var scope = Scope(); var profile = OutputProfile; var onlyEpisodes = scope.Kind == LibraryMediaKind.Series && (selected.File != null || selected.Season < 0);
        var report = await Work(async () =>
        {
            var progress = WorkProgress("Update metadata");
            var result = await Task.Run(() => _services.Output().UpdateMetadataAsync(scope, profile, onlyEpisodes, selected.Season is >= 0 ? selected.Season : null, progress));
            await RefreshWrittenMedia(selected.Media); return result;
        });
        _status.Text = L.Format($"NFO updated: {report.Written}."); if (report.Warnings.Count > 0) _status.Text += " · " + string.Join(" · ", report.Warnings);
    }

    private async Task Scrape()
    {
        if (Selected is not { } selected) return; var scope = Scope(); if (scope.Files.All(f => f.Exclusion.Length > 0)) throw new InvalidOperationException(L.Text(scope.Files.Any(f => f.NeedsReview) ? "Match this special to a source episode before scraping." : "Select an included episode first."));
        if (TmdbCredentials.NeedsConfiguration(scope.Matches.Values.Select(m => m.Document.Provider).Concat(scope.Documents.Values.Select(d => d.Provider)), _config.GetApiKey()) && !await TmdbKeyPrompt.Configure(_config, XamlRoot)) return;
        var outputProfile = OutputProfile; var content = new StackPanel { Spacing = 12, Width = 530 }; content.Children.Add(Text(selected.File?.Name ?? (selected.Season is >= 0 ? L.Format($"Season {selected.Season.Value}") : selected.Media.Title), 18));
        var profileLabel = Text(L.Text("Media tool") + ": " + outputProfile.Name, 12); profileLabel.Style = Ui.Style("SecondaryTextStyle"); content.Children.Add(profileLabel);
        var documents = selected.File is { } selectedFile ? new[] { EpisodeMatching.Resolve(scope, selectedFile)?.Document }.OfType<MetadataDocument>() : selected.Season is >= 0 ? scope.Files.Select(f => EpisodeMatching.Resolve(scope, f)?.Document).OfType<MetadataDocument>() : scope.Matches.Values.Select(m => m.Document);
        var sources = new StackPanel { Spacing = 8 };
        foreach (var doc in documents.DistinctBy(d => (d.Provider, d.Id)))
        {
            sources.Children.Add(Text(doc.Title));
            sources.Children.Add(Ui.SourceLinks(doc, scope.Kind));
        }
        content.Children.Add(new ScrollViewer { Content = sources, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text(selected.File != null ? "Scrape this episode" : "Scrape selected"), Content = content, PrimaryButtonText = L.Text("Start scraping"), CloseButtonText = L.Text("Cancel") }; if (await Ui.Show(dialog) != ContentDialogResult.Primary) return;
        var onlyEpisodes = selected.Media.Kind == LibraryMediaKind.Series && (selected.File != null || selected.Season < 0); var seasonOnly = selected.Season is >= 0 ? selected.Season : null;
        var report = await Work(async () => { var progress = WorkProgress("Scraping"); var result = await Task.Run(() => _services.Output().ScrapeAsync(scope, outputProfile, episodesOnly: onlyEpisodes, progress: progress, seasonOnly: seasonOnly)); foreach (var doc in scope.Matches.Values.Select(m => m.Document)) _store.Documents[EpisodeMatching.Key(doc, scope.Kind)] = doc; await Save(); await RefreshWrittenMedia(selected.Media); return result; }); _status.Text = L.Format($"NFO updated: {report.Written}; images downloaded: {report.Images}."); if (report.Warnings.Count > 0) _status.Text += " · " + string.Join(" · ", report.Warnings);
    }

    private async Task PreviewRename()
    {
        if (Selected is not { } selected) return;
        var media = selected.Media; var scope = Scope(); var library = _libraries.SelectedItem as MediaLibrary;
        var renameWork = selected.File == null && selected.Season == null && !(library is { IsRoot: true } && library.Path.Equals(media.Folder, StringComparison.OrdinalIgnoreCase));
        var dialog = new RenamePreviewDialog(scope, scope.Kind == LibraryMediaKind.Series ? _config.GetEpisodeRenameTemplate() : _config.GetLastTemplate(), OutputProfile, renameWork, _config.GetWorkFolderRenameTemplate()) { XamlRoot = XamlRoot };
        if (await Ui.Show(dialog) != ContentDialogResult.Primary) { _status.Text = L.Text("Rename cancelled."); return; }
        var plan = dialog.SelectedPlan; var folder = dialog.SelectedFolder;
        if (scope.Kind == LibraryMediaKind.Series) _config.SetEpisodeRenameTemplate(dialog.NamingTemplate); else _config.SetLastTemplate(dialog.NamingTemplate);
        if (dialog.RenamesWork) _config.SetWorkFolderRenameTemplate(dialog.FolderNamingTemplate);
        await Work(async () =>
        {
            // Prepare every path mapping before touching files, including offline snapshots.
            var updatedState = System.Text.Json.JsonSerializer.Deserialize<LibraryStateStore>(System.Text.Json.JsonSerializer.Serialize(_store))!;
            LibraryPathMigration.Apply(updatedState, plan, folder);
            var snapshots = new List<(MediaLibrary Library, LibrarySnapshot Snapshot)>();
            foreach (var savedLibrary in _store.Libraries)
            {
                var snapshot = _snapshots.GetValueOrDefault(LibrarySnapshotStore.Key(savedLibrary)) ?? await Task.Run(() => _snapshotStore.Load(savedLibrary));
                if (snapshot == null) continue;
                var copy = await Task.Run(() => LibrarySnapshotStore.CopyMedia(snapshot));
                foreach (var item in copy) LibraryPathMigration.Apply(item, plan, folder);
                snapshots.Add((savedLibrary with { Path = LibraryPathMigration.MoveRoot(savedLibrary.Path, folder) }, snapshot with { Media = copy }));
            }
            await Task.Run(() => _rename.Execute(plan, folder));
            _store = updatedState; _expanded = _store.ExpandedRows.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Save();
            foreach (var entry in snapshots) { await Task.Run(() => _snapshotStore.Save(entry.Library, entry.Snapshot)); _snapshots[LibrarySnapshotStore.Key(entry.Library)] = entry.Snapshot; }
            foreach (var item in _media) LibraryPathMigration.Apply(item, plan, folder);
            _initializing = true;
            try { _libraries.ItemsSource = _store.Libraries; _libraries.SelectedItem = library == null ? null : _store.Libraries.First(l => l.Name == library.Name && l.Path == LibraryPathMigration.MoveRoot(library.Path, folder)); }
            finally { _initializing = false; }
            _activeLibraryKey = _store.LastLibraryKey;
            await RefreshWrittenMedia(media); return true;
        });
        _status.Text = folder == null ? L.Format($"Renamed {plan.Count} files.") : L.Text("Rename completed.");
    }

    private async Task MissingEpisodes()
    {
        if (Current is not { } media) return; var occupied = media.Files.Where(f => f.Exclusion.Length == 0).Select(f => EpisodeMatching.Resolve(media, f)).Where(c => c != null).Select(c => (c!.SourceKey, c.Episode.Id)).ToHashSet(); var missing = EpisodeMatching.Choices(media.Documents.Values, media.Kind).Where(c => !occupied.Contains((c.SourceKey, c.Episode.Id))).ToList(); var table = new DenseTable { Width = 850, MaxHeight = 500 }; table.SetColumns(new[] { ("S / E", 90d), (L.Text("Episode title"), 400d), (L.Text("Metadata source"), 330d) }); table.List.ItemsSource = missing.Select(c => new TableRow { Cells = new() { new($"S{c.Episode.Season:00}E{c.Episode.Number:00}", 90), new(c.Episode.Title, 400), new(c.Document.Provider + " · " + c.Document.Title, 330) } }).ToList(); var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Format($"Missing source episodes: {missing.Count}"), Content = table, CloseButtonText = L.Text("Close") }; dialog.Resources["ContentDialogMaxWidth"] = 1000d; await Ui.Show(dialog);
    }
}
