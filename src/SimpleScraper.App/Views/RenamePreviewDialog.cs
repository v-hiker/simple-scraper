using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleScraper.Views;

public sealed class RenamePreviewDialog : ContentDialog
{
    private readonly LibraryMedia _media;
    private readonly LibraryRenameService _service = new();
    private readonly TextBox _template = new();
    private readonly TextBox _folderTemplate = new();
    private readonly TextBlock _folderPaths = new() { TextWrapping = TextWrapping.Wrap };
    private readonly bool _renameWork;
    private FolderRename? _folderPlan;
    private readonly TextBox _filter = new() { Width = 230 };
    private readonly ComboBox _state = new() { MinWidth = 130 };
    private readonly DenseTable _rows = new();
    private readonly ProgressRing _loading = new() { Width = 22, Height = 22 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Style = Ui.Style("SecondaryTextStyle") };
    private List<RenamePreviewGroup> _groups = new();
    private bool _valid;
    private bool _closed;
    private int _templateRevision;
    private double _pathWidth = 420, _tableWidth = 984;
    public List<FileRename> SelectedPlan { get; private set; } = new();
    public FolderRename? SelectedFolder { get; private set; }
    public string NamingTemplate => _template.Text;
    public string FolderNamingTemplate => _folderTemplate.Text;
    public bool RenamesWork => _renameWork;
    public MediaOutputProfile OutputProfile { get; }
    public RenamePreviewDialog(LibraryMedia media, string template, MediaOutputProfile? profile = null, bool renameWork = false, string? folderTemplate = null)
    {
        _media = media; _renameWork = renameWork; OutputProfile = profile ?? MediaOutputProfile.Jellyfin; Title = L.Text("Preview rename"); PrimaryButtonText = L.Text("Execute rename"); CloseButtonText = L.Text("Cancel"); IsPrimaryButtonEnabled = false; Resources["ContentDialogMaxWidth"] = 1200d;
        Ui.TrackTheme(this, new ConfigService().GetUiTheme());
        var panel = new Grid { Width = 1000, MaxHeight = 650, RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) panel.RowDefinitions.Add(new() { Height = height });
        var heading = new Grid { ColumnSpacing = 16 }; heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var work = new StackPanel { Spacing = 8 }; work.Children.Add(T(L.Text("Library folder") + ": " + media.Folder));
        if (_renameWork)
        {
            _folderTemplate.Header = L.Text("Folder rename template"); _folderTemplate.Text = folderTemplate ?? LibraryRenameService.WorkFolderTemplate;
            var folderEdit = new Grid { ColumnSpacing = 8 }; folderEdit.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); folderEdit.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); folderEdit.Children.Add(_folderTemplate);
            var resetFolder = new Button { Content = L.Text("Use default template"), VerticalAlignment = VerticalAlignment.Bottom }; resetFolder.Click += (_, _) => _folderTemplate.Text = LibraryRenameService.WorkFolderTemplate; Grid.SetColumn(resetFolder, 1); folderEdit.Children.Add(resetFolder);
            work.Children.Add(folderEdit); work.Children.Add(_folderPaths); Id(_folderTemplate, "RenameWorkFolder");
        }
        heading.Children.Add(work);
        var profileLabel = T(L.Text("Media tool") + ": " + OutputProfile.Name, 13); Id(profileLabel, "RenameOutputProfile"); profileLabel.Style = Ui.Style("SecondaryTextStyle"); Grid.SetColumn(profileLabel, 1); heading.Children.Add(profileLabel); panel.Children.Add(heading);
        var edit = new Grid { ColumnSpacing = 8 }; edit.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); edit.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); edit.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _template.Header = L.Text("File rename template"); _template.Text = template; edit.Children.Add(_template);
        var presets = new Button { Content = L.Text("Use default template"), VerticalAlignment = VerticalAlignment.Bottom }; presets.Click += (_, _) => _template.Text = media.Kind == LibraryMediaKind.Series ? LibraryRenameService.EpisodeTemplate : LibraryRenameService.MovieTemplate; Grid.SetColumn(presets, 1); edit.Children.Add(presets);
        _refresh.Content = L.Text("Refresh preview"); _refresh.VerticalAlignment = VerticalAlignment.Bottom; _refresh.Click += async (_, _) => await Generate(); Grid.SetColumn(_refresh, 2); edit.Children.Add(_refresh); Grid.SetRow(edit, 1); panel.Children.Add(edit);
        UpdateHint(); Grid.SetRow(_hint, 2); panel.Children.Add(_hint);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _state.ItemsSource = new[] { L.Text("All files"), L.Text("Changed"), L.Text("Conflicts"), L.Text("Skipped") }; _state.SelectedIndex = 1; _filter.PlaceholderText = L.Text("Filter filenames");
        controls.Children.Add(_state); controls.Children.Add(_filter);
        var all = new Button { Content = L.Text("Select visible changes") }; all.Click += (_, _) => { RenameReview.SelectVisible(_groups, _state.SelectedIndex, _filter.Text, true); ShowRows(); }; controls.Children.Add(all);
        var none = new Button { Content = L.Text("Clear visible selection") }; none.Click += (_, _) => { RenameReview.SelectVisible(_groups, _state.SelectedIndex, _filter.Text, false); ShowRows(); }; controls.Children.Add(none);
        Grid.SetRow(controls, 3); panel.Children.Add(controls);
        SetColumns();
        _rows.MinHeight = 280; Grid.SetRow(_rows, 4); panel.Children.Add(_rows);
        var feedback = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; feedback.Children.Add(_loading); feedback.Children.Add(_status); Grid.SetRow(feedback, 5); panel.Children.Add(feedback); Content = panel;
        _filter.TextChanged += (_, _) => ShowRows(); _state.SelectionChanged += (_, _) => ShowRows();
        _template.TextChanged += async (_, _) => { var revision = ++_templateRevision; _valid = false; IsPrimaryButtonEnabled = false; ShowRows(); await Task.Delay(450); if (!_closed && IsLoaded && revision == _templateRevision) await Generate(); };
        _folderTemplate.TextChanged += async (_, _) => { var revision = ++_templateRevision; _valid = false; IsPrimaryButtonEnabled = false; await Task.Delay(450); if (!_closed && IsLoaded && revision == _templateRevision) await Generate(); };
        Loaded += async (_, _) => { panel.Width = Math.Min(1100, Math.Max(620, XamlRoot.Size.Width - 100)); panel.MaxHeight = Math.Max(420, Math.Min(720, XamlRoot.Size.Height - 170)); _tableWidth = panel.Width - 16; _pathWidth = (_tableWidth - 144) / 2; SetColumns(); Ui.Enter(panel); await Generate(); }; Closed += (_, _) => _closed = true;
        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                SelectedPlan = _groups.Where(g => g.Selected && g.State == RenamePreviewState.Changed).SelectMany(g => g.Operations).ToList();
                SelectedFolder = _folderPlan;
                if (!_valid || SelectedPlan.Count == 0 && SelectedFolder == null) throw new InvalidOperationException(L.Text("Select at least one change."));
                await Task.Run(() => LibraryRenameService.Validate(SelectedPlan, SelectedFolder));
            }
            catch (Exception e) { args.Cancel = true; _status.Text = e.Message; IsPrimaryButtonEnabled = false; _valid = false; }
            finally { deferral.Complete(); }
        };
        Id(_template, "RenameTemplate"); Id(_refresh, "RenameRefresh"); Id(_state, "RenameFilter"); Id(_rows.List, "RenameRows"); Id(_status, "RenameSummary"); Id(all, "RenameSelectAll"); Id(none, "RenameSelectNone");
    }
    private void SetColumns() => _rows.SetColumns(new[] { ("", 34d), (L.Text("Original path"), _pathWidth), (L.Text("New path"), _pathWidth), (L.Text("State"), 110d) });
    private void UpdateHint() => _hint.Text = L.Text("Tokens: {Title}, {Year}, {Season}, {Episode}, {EpisodeTitle}.") + (_media.Kind == LibraryMediaKind.Series ? "\n" + L.Text("Matched episodes are organized into Season 01, Season 02, etc. Existing matching season folders are preserved.") + "\n" + L.Text("Specials folder") + ": " + OutputProfile.SpecialsFolder + " · " + L.Text("Specials are organized only when output numbering is S00. Matching alone does not move files.") : "") + "\n" + L.Text("OP, ED and Menu videos are organized as extras. They do not use the episode template or S00 numbering.");
    private async Task Generate()
    {
        if (!_refresh.IsEnabled) return;
        _refresh.IsEnabled = false; _loading.IsActive = true; _valid = false; IsPrimaryButtonEnabled = false; _status.Text = L.Text("Working…"); var template = _template.Text; var profile = OutputProfile; var folderName = _folderTemplate.Text;
        try
        {
            var groups = await Task.Run(() => _service.BuildPreview(_media, template, profile));
            var folderPlan = _renameWork ? LibraryRenameService.PreviewFolder(_media, LibraryRenameService.WorkFolderName(_media, folderName)) : null;
            if (folderPlan != null) await Task.Run(() => LibraryRenameService.Validate(Array.Empty<FileRename>(), folderPlan));
            if (_closed) return;
            var choices = _groups.ToDictionary(g => g.File.Path, g => g.Selected, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.Where(g => g.State == RenamePreviewState.Changed)) if (choices.TryGetValue(group.File.Path, out var selected)) group.Selected = selected;
            _groups = groups; _folderPlan = folderPlan; _folderPaths.Text = _renameWork ? _media.Folder + "\n→ " + (folderPlan?.Destination ?? _media.Folder) : ""; _valid = template == _template.Text && folderName == _folderTemplate.Text;
            if (_state.SelectedIndex == 1 && !groups.Any(g => g.State == RenamePreviewState.Changed)) _state.SelectedIndex = groups.Any(g => g.State == RenamePreviewState.Conflict) ? 2 : 0;
            ShowRows();
        }
        catch (Exception e) { if (!_closed) { _groups.Clear(); _rows.List.ItemsSource = null; _status.Text = e.Message; } }
        finally { if (!_closed) { _refresh.IsEnabled = true; _loading.IsActive = false; if (template != _template.Text || folderName != _folderTemplate.Text) await Generate(); } }
    }
    private void ShowRows()
    {
        var rows = new List<TableRow>(); string? folder = null;
        foreach (var g in RenameReview.Visible(_groups, _state.SelectedIndex, _filter.Text))
        {
            var currentFolder = Path.GetRelativePath(_media.Folder, Path.GetDirectoryName(g.File.Path)!);
            if (folder != currentFolder) { folder = currentFolder; rows.Add(new() { Header = true, Cells = new() { new(folder == "." ? Path.GetFileName(_media.Folder) : folder, _tableWidth) { Wrap = true } } }); }
            var state = L.Text(g.State switch { RenamePreviewState.Changed => "Changed", RenamePreviewState.Conflict => "Conflicts", RenamePreviewState.Unchanged => "Unchanged", RenamePreviewState.Excluded => "Excluded", _ => "Not matched" });
            var video = g.Operations.FirstOrDefault(p => p.Source == g.File.Path);
            if (g.State == RenamePreviewState.Changed && video != null && !string.Equals(Path.GetDirectoryName(video.Source), Path.GetDirectoryName(video.Destination), StringComparison.OrdinalIgnoreCase)) state = L.Text(g.IsExtra ? "Move to extras" : g.File.Season == 0 ? "Move to specials" : "Move to season");
            rows.Add(new() { Tag = g, Checked = g.Selected, CheckEnabled = _valid && g.State == RenamePreviewState.Changed, CheckChanged = value => { g.Selected = value; Summary(); }, Cells = new() { new("", 34), new(DisplayPath(g.File.Path), _pathWidth) { Wrap = true }, new(DisplayPath(LibraryPathMigration.MoveRoot(video?.Destination ?? g.File.Path, _folderPlan)), _pathWidth, g.State == RenamePreviewState.Changed || _folderPlan != null) { Wrap = true }, new(state, 110) { Wrap = true, Tone = g.State == RenamePreviewState.Changed ? CellTone.Success : g.State == RenamePreviewState.Conflict ? CellTone.Critical : g.State == RenamePreviewState.Unmatched ? CellTone.Warning : CellTone.Muted, Glyph = g.State == RenamePreviewState.Conflict ? "\uE7BA" : "" } } });
            foreach (var path in g.RelatedFiles)
            {
                var op = g.Operations.FirstOrDefault(p => p.Source == path);
                rows.Add(new() { Tag = g, Cells = new() { new("", 34), new((op == null ? "  " : "− ") + DisplayPath(path), _pathWidth, Removed: op != null) { Wrap = true }, new((op == null ? "  " : "+ ") + DisplayPath(LibraryPathMigration.MoveRoot(op?.Destination ?? path, _folderPlan)), _pathWidth, op != null || _folderPlan != null) { Wrap = true }, new(op == null ? L.Text("Preserved") : Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), 110) { Wrap = true } } });
            }
            if (g.Detail.Length > 0) rows.Add(new() { Cells = new() { new("  " + g.Detail, _tableWidth) { Wrap = true } } });
        }
        _rows.List.ItemsSource = rows;
        Summary();
    }
    private string DisplayPath(string path) => Path.GetRelativePath(_folderPlan == null ? _media.Folder : Path.GetDirectoryName(_media.Folder)!, path);
    private void Summary()
    {
        var selected = _groups.Where(g => g.Selected && g.State == RenamePreviewState.Changed).ToList();
        PrimaryButtonText = L.Text("Execute rename") + $" ({selected.Sum(g => g.Operations.Count) + (_folderPlan == null ? 0 : 1)})"; IsPrimaryButtonEnabled = _valid && (selected.Count > 0 || _folderPlan != null);
        var visible = RenameReview.Visible(_groups, _state.SelectedIndex, _filter.Text).ToHashSet();
        _status.Text = _valid ? L.Format($"Selected videos: {selected.Count}; total files: {selected.Sum(g => g.Operations.Count)}; conflicts: {_groups.Count(g => g.State == RenamePreviewState.Conflict)}; skipped: {_groups.Count(g => g.State is RenamePreviewState.Excluded or RenamePreviewState.Unmatched or RenamePreviewState.Unchanged)}.") + "\n" + L.Format($"Selected outside this filter: {selected.Count(g => !visible.Contains(g))}.") + " " + L.Text("Video and companion files are selected together. Conflicts are never overwritten.") : L.Text("Updating preview…");
    }
    private static TextBlock T(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static void Id(DependencyObject element, string id) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);
}
