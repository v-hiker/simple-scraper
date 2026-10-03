using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using System.Xml.Linq;
using System.Diagnostics;

namespace SimpleScraper.Views;

public sealed partial class LibraryPage
{
    private Task Scan() => LoadLibrary(true);

    private async Task LoadLibrary(bool refresh = false)
    {
        if (_libraries.SelectedItem is not MediaLibrary library) return;
        var operation = Guid.NewGuid().ToString("N"); var totalTiming = Stopwatch.StartNew(); var timing = new Stopwatch();
        var operationMediaCount = 0; int? operationRowCount = null; var outcome = "superseded";
        _scanCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); _scanCancellation = cancellation; var version = ++_scanVersion; var key = LibrarySnapshotStore.Key(library); _loadingLibrary = true; _scanning = refresh; _progress.IsIndeterminate = true; UpdateActions();
        var progress = new Progress<LibraryScanProgress>(p => { if (version != _scanVersion || !_scanning || cancellation.IsCancellationRequested) return; _progress.IsIndeterminate = false; _progress.Maximum = Math.Max(1, p.Total); _progress.Value = p.Completed; _status.Text = L.Format($"Reading library: {p.Completed}/{p.Total}") + " · " + Path.GetFileName(p.Folder); });
        try
        {
            if (_activeLibraryKey != key) { _media = new(); _episodeCache.Clear(); await ShowRows(); }
            if (version != _scanVersion) return;
            _status.Text = L.Text("Reading saved library…");
            LibrarySnapshot? snapshot = null;
            if (!refresh)
            {
                timing.Restart(); snapshot = _snapshots.GetValueOrDefault(key) ?? await Task.Run(() => _snapshotStore.Load(library), cancellation.Token);
                _services.WritePerformance(operation, "cache-load", timing.Elapsed.TotalMilliseconds, snapshot?.Media.Count ?? 0, refresh: refresh);
            }
            if (version != _scanVersion) return; cancellation.Token.ThrowIfCancellationRequested();
            var scanned = snapshot == null;
            operationMediaCount = snapshot?.Media.Count ?? 0;
            if (snapshot == null)
            {
                _scanning = true; UpdateActions();
                timing.Restart();
                var raw = await Task.Run(() => _scanner.Scan(library, cancellation.Token, progress, _store.MediaKinds), cancellation.Token);
                operationMediaCount = raw.Count;
                _services.WritePerformance(operation, "scan", timing.Elapsed.TotalMilliseconds, raw.Count, refresh: refresh);
                if (version != _scanVersion) return; cancellation.Token.ThrowIfCancellationRequested();
                snapshot = new(1, DateTimeOffset.UtcNow, raw); var saved = snapshot;
                timing.Restart();
                await Task.Run(() => _snapshotStore.Save(library, saved));
                _services.WritePerformance(operation, "cache-save", timing.Elapsed.TotalMilliseconds, raw.Count, refresh: refresh);
            }
            if (version != _scanVersion) return; cancellation.Token.ThrowIfCancellationRequested();
            _snapshots[key] = snapshot;
            timing.Restart();
            var source = snapshot; var media = await Task.Run(() => { var copy = LibrarySnapshotStore.CopyMedia(source, library.Kind); LibrarySnapshotStore.ApplyRecords(copy, _store); return copy; }, cancellation.Token);
            _services.WritePerformance(operation, "copy-apply", timing.Elapsed.TotalMilliseconds, media.Count, refresh: refresh);
            if (version != _scanVersion) return; cancellation.Token.ThrowIfCancellationRequested();
            _media = media; _activeLibraryKey = key; _store.LastLibraryKey = key; _episodeCache.Clear(); await ShowRows(operation: operation, refresh: refresh, cancellationToken: cancellation.Token);
            if (version != _scanVersion) return; cancellation.Token.ThrowIfCancellationRequested();
            operationRowCount = _visibleRows.Count;
            timing.Restart(); await Save();
            _services.WritePerformance(operation, "state-save", timing.Elapsed.TotalMilliseconds, media.Count, _visibleRows.Count, refresh);
            if (version == _scanVersion) { outcome = "completed"; _status.Text = scanned ? L.Format($"Updated and saved {_media.Count} movies or series.") : L.Format($"Loaded {_media.Count} cached movies or series. Last update: {snapshot.SavedAt.LocalDateTime:g}. Click Refresh library to rescan."); }
        }
        catch (OperationCanceledException) { outcome = "cancelled"; if (version == _scanVersion) _status.Text = L.Text(_media.Count > 0 ? "Loading cancelled. Previously loaded rows are still available." : "Loading cancelled. Select a library or refresh to try again."); }
        catch (Exception e) { outcome = "failed"; if (version == _scanVersion) _status.Text = e.Message; }
        finally { if (cancellation.IsCancellationRequested) outcome = "cancelled"; _services.WritePerformance(operation, "load-total", totalTiming.Elapsed.TotalMilliseconds, operationMediaCount, operationRowCount, refresh, outcome); cancellation.Dispose(); if (version == _scanVersion) { _scanCancellation = null; _scanning = _loadingLibrary = false; UpdateActions(); } }
    }

    private async Task RefreshWrittenMedia(LibraryMedia media)
    {
        var raw = await Task.Run(() => _scanner.Scan(media.Folder, media.Kind, false).Single());
        foreach (var library in _store.Libraries)
        {
            var key = LibrarySnapshotStore.Key(library); var snapshot = _snapshots.GetValueOrDefault(key) ?? await Task.Run(() => _snapshotStore.Load(library));
            if (snapshot == null || !snapshot.Media.Any(m => m.Folder.Equals(raw.Folder, StringComparison.OrdinalIgnoreCase))) continue;
            var updated = snapshot with { SavedAt = DateTimeOffset.UtcNow, Media = snapshot.Media.Select(m => m.Folder.Equals(raw.Folder, StringComparison.OrdinalIgnoreCase) ? raw : m).ToList() };
            await Task.Run(() => _snapshotStore.Save(library, updated)); _snapshots[key] = updated;
        }
        var view = await Task.Run(() => LibrarySnapshotStore.CopyMedia(new(1, DateTimeOffset.UtcNow, new() { raw })).Single()); LibrarySnapshotStore.ApplyRecords(new[] { view }, _store); _media = _media.Select(m => m.Folder.Equals(view.Folder, StringComparison.OrdinalIgnoreCase) ? view : m).ToList(); _episodeCache.Clear(); await ShowRows();
    }

    private Task Save() => Task.Run(_store.Save);
    private async Task AddLibrary()
    {
        var name = new TextBox { Header = L.Text("Library name"), PlaceholderText = L.Text("Library name") }; Id(name, "NewLibraryName"); var path = new TextBox { PlaceholderText = L.Text("Folder path") }; Id(path, "NewLibraryPath"); var type = new ComboBox { ItemsSource = new[] { L.Text("Movies"), L.Text("TV series"), L.Text("Mixed / auto detect") }, SelectedIndex = 2 }; Id(type, "NewLibraryKind");
        var profile = new ComboBox { ItemsSource = new[] { "Jellyfin", "Emby" }, SelectedIndex = _config.GetDefaultOutputProfile() == "Emby" ? 1 : 0 }; Id(profile, "NewLibraryProfile");
        var pathRow = new Grid { ColumnSpacing = 8 }; pathRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); pathRow.Children.Add(path);
        var browse = Button("Browse", async () => { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)); var folder = await picker.PickSingleFolderAsync(); if (folder != null) { path.Text = folder.Path; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.Path)); } }, "NewLibraryBrowse"); Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
        var panel = new StackPanel { Spacing = 12, Width = 520 }; panel.Children.Add(name); panel.Children.Add(Text("Folder path")); panel.Children.Add(pathRow); panel.Children.Add(InlineField("Media type", type)); panel.Children.Add(InlineField("Media tool", profile)); var hint = Text("Automatically recognizes a library folder or a single movie / series folder. No scanning mode selection is needed.", 12); hint.Style = Ui.Style("SecondaryTextStyle"); panel.Children.Add(hint);
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error }; Id(error, "NewLibraryError"); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Add library"), Content = panel, PrimaryButtonText = L.Text("Add"), CloseButtonText = L.Text("Cancel") }; dialog.Resources["ContentDialogMaxWidth"] = 620d;
        MediaLibrary? entry = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); dialog.IsPrimaryButtonEnabled = false; error.IsOpen = false;
            try
            {
                var selectedPath = path.Text.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(selectedPath)) throw new ArgumentException(L.Text("Choose a media folder first."));
                var fullPath = Path.GetFullPath(selectedPath);
                if (!await Task.Run(() => Directory.Exists(fullPath))) throw new DirectoryNotFoundException(L.Text("The folder is unavailable. Check the path or reconnect the drive."));
                var isRoot = await Task.Run(() => LibraryScanner.DetectLibraryRoot(fullPath));
                var candidate = new MediaLibrary(string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)) : name.Text.Trim(), fullPath, (LibraryMediaKind)type.SelectedIndex, isRoot) { AutoLayout = true, OutputProfile = profile.SelectedIndex == 1 ? "Emby" : "Jellyfin" };
                if (LibraryReview.DuplicateLibrary(_store.Libraries, candidate)) throw new ArgumentException(L.Text("This folder is already in your libraries with the same type and scanning mode."));
                entry = candidate;
            }
            catch (Exception e) { args.Cancel = true; error.Message = e.Message; error.IsOpen = true; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        if (await Ui.Show(dialog) != ContentDialogResult.Primary || entry == null) return;
        _store.Libraries = _store.Libraries.Append(entry).ToList(); await Save(); _libraries.ItemsSource = _store.Libraries; _libraries.SelectedItem = entry;
    }

    private async Task ConfigureLibrary()
    {
        if (_libraries.SelectedItem is not MediaLibrary library) return;
        var name = new TextBox { Header = L.Text("Library name"), Text = library.Name }; Id(name, "LibraryName");
        var profile = new ComboBox { ItemsSource = new[] { "Jellyfin", "Emby" }, SelectedIndex = OutputProfile == MediaOutputProfile.Emby ? 1 : 0 }; Id(profile, "LibraryOutputProfile");
        var rules = Text(""); void Describe() => rules.Text = (profile.SelectedIndex == 1 ? MediaOutputProfile.Emby : MediaOutputProfile.Jellyfin).Describe(); Describe(); profile.SelectionChanged += (_, _) => Describe();
        var panel = new StackPanel { Spacing = 12, Width = 530 }; panel.Children.Add(Text(library.Path)); panel.Children.Add(Text(library.KindLabel)); panel.Children.Add(name); panel.Children.Add(InlineField("Media tool", profile)); panel.Children.Add(rules); panel.Children.Add(Text("Scraping and renaming use this library's media tool. Changing this setting does not modify existing files."));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Library settings"), Content = panel, PrimaryButtonText = L.Text("Apply"), SecondaryButtonText = L.Text("Remove library"), SecondaryButtonStyle = Ui.Style("DangerButtonStyle"), CloseButtonText = L.Text("Cancel"), DefaultButton = ContentDialogButton.Primary };
        dialog.Resources["ContentDialogMaxWidth"] = 620d;
        var result = await Ui.Show(dialog);
        if (result == ContentDialogResult.Secondary) { await ConfirmRemoveLibrary(library); return; }
        if (result != ContentDialogResult.Primary) return;
        var updated = library with { Name = string.IsNullOrWhiteSpace(name.Text) ? library.Name : name.Text.Trim(), OutputProfile = profile.SelectedIndex == 1 ? "Emby" : "Jellyfin" };
        _store.Libraries = _store.Libraries.Select(l => l == library ? updated : l).ToList(); await Save();
        _initializing = true;
        try { _libraries.ItemsSource = _store.Libraries; _libraries.SelectedItem = updated; }
        finally { _initializing = false; }
    }

    private async Task ConfirmRemoveLibrary(MediaLibrary library)
    {
        var content = new StackPanel { Spacing = 12, Width = 530 }; content.Children.Add(Text(library.Name, 18)); content.Children.Add(Text(library.Path)); content.Children.Add(Text("Remove this library and its scan cache from the app? Media files, NFO files and pictures in this folder will not be deleted."));
        var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Remove library"), Content = content, PrimaryButtonText = L.Text("Remove library"), PrimaryButtonStyle = Ui.Style("DangerButtonStyle"), CloseButtonText = L.Text("Cancel"), DefaultButton = ContentDialogButton.Close }; Id(confirm, "LibraryRemoveConfirmation"); confirm.Resources["ContentDialogMaxWidth"] = 620d;
        if (await Ui.Show(confirm) != ContentDialogResult.Primary) return;
        _scanCancellation?.Cancel(); _scanCancellation = null; _scanning = _loadingLibrary = false; _scanVersion++; _snapshots.Remove(LibrarySnapshotStore.Key(library)); await Task.Run(() => _snapshotStore.Remove(library)); _store.Libraries = _store.Libraries.Where(l => l != library).ToList(); _activeLibraryKey = null; _store.LastLibraryKey = ""; await Save();
        _initializing = true;
        try { _libraries.ItemsSource = _store.Libraries; _libraries.SelectedIndex = _store.Libraries.Count > 0 ? 0 : -1; }
        finally { _initializing = false; }
        _media.Clear(); await ShowRows(); if (_store.Libraries.Count > 0) await LoadLibrary();
    }

    private async Task ConfigureColumns()
    {
        var preferences = new System.Collections.ObjectModel.ObservableCollection<LibraryColumnPreference>(LibraryColumns.Normalize(_store.Columns));
        var rows = new ListView { ItemsSource = preferences, CanDragItems = true, CanReorderItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 450, ItemTemplate = (DataTemplate)Application.Current.Resources["ColumnPreferenceTemplate"], ItemContainerStyle = Ui.Style("FluentTableItemStyle") }; Id(rows, "ColumnPreferences");
        var panel = new StackPanel { Spacing = 12, Width = 530 }; panel.Children.Add(Text("Drag rows to reorder columns. Drag header dividers in the library to resize. Title is always visible.")); panel.Children.Add(rows);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Text("Columns"), Content = panel, PrimaryButtonText = L.Text("Apply"), SecondaryButtonText = L.Text("Restore defaults"), CloseButtonText = L.Text("Cancel") }; var result = await Ui.Show(dialog); if (result == ContentDialogResult.None) return; _store.Columns = result == ContentDialogResult.Secondary ? LibraryColumns.Normalize(null) : LibraryColumns.Normalize(preferences); await Save(); await ShowRows(true);
    }

    private async Task OpenSettings()
    {
        var dialog = new SettingsDialog(_config) { XamlRoot = XamlRoot };
        await Ui.Show(dialog);
        if (!dialog.Saved) return;
        if (dialog.LanguageChanged) _status.Text = L.Language == "zh-CN" ? "设置已保存，界面语言将在下次启动时生效。" : "Settings saved. The interface language changes on the next launch.";
    }
}
