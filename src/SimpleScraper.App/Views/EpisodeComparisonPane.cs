using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleScraper.Views;

internal sealed class EpisodeComparisonPane : Grid
{
    private readonly LibraryMedia _media;
    private readonly string? _focusFile;
    private readonly DenseTable _local = new();
    private readonly ComboBox _order = new();
    private readonly ComboBox _fileScope = new() { Header = L.Text("Local file range"), MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _sourceScope = new() { Header = L.Text("Source episodes"), MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _suggest = new() { Content = Ui.Label("\uE8AB", "Suggest by numbering") };
    private bool _settingContext;
    private readonly NumberBox _season = new() { Header = L.Text("Season"), Minimum = 0, Maximum = 999, Width = 100 };
    private readonly NumberBox _number = new() { Header = L.Text("Episode"), Minimum = 1, Maximum = 9999, Width = 100 };
    private readonly TextBlock _context = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = Ui.Label("\uE70F", "Apply output numbering"), VerticalAlignment = VerticalAlignment.Bottom };
    private double _fileWidth = 340, _episodeWidth = 440;
    private MetadataDocument? _activeDocument;
    private HashSet<int> _localSeasons = new();
    private int? _remoteSeason;
    private sealed record PickerOption(string Label, EpisodeChoice? Choice = null, bool Skip = false) { public override string ToString() => Label; }
    private List<PickerOption> _pickerOptions = new();
    private LocalMediaFile? SelectedFile => (_local.List.SelectedItem as TableRow)?.Tag as LocalMediaFile;
    public bool HasResult => _activeDocument != null;
    public Dictionary<int, MetadataMatch> Matches { get; }
    public Dictionary<string, MetadataDocument> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EpisodeBinding> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;

    public EpisodeComparisonPane(LibraryMedia media, string? focusFile = null)
    {
        _media = media; _focusFile = focusFile; Matches = new(media.Matches);
        foreach (var doc in media.Documents.Values.Concat(media.Matches.Values.Select(m => m.Document))) Merge(doc);
        foreach (var file in media.Files)
        {
            if (file.Exclusion.Length > 0 && !file.NeedsReview) Excluded.Add(file.Path);
            else if (EpisodeMatching.Resolve(media, file) is { } choice) Bindings[file.Path] = new(choice.SourceKey, choice.Episode, file.Season, file.Episode);
        }
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) RowDefinitions.Add(new() { Height = height });
        RowSpacing = 10; Children.Add(_context);
        var filters = new Grid { ColumnSpacing = 12 }; filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        filters.Children.Add(_fileScope); Grid.SetColumn(_sourceScope, 1); filters.Children.Add(_sourceScope); Grid.SetRow(filters, 1); Children.Add(filters);
        _fileScope.SelectionChanged += (_, _) => { if (!_settingContext) UpdateScope(); };
        _sourceScope.SelectionChanged += (_, _) => { if (!_settingContext) UpdateScope(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _order.ItemsSource = new[] { L.Text("Aired order (season / episode)"), L.Text("Absolute order (regular episodes)") }; _order.SelectedIndex = 0; bar.Children.Add(_order);
        _suggest.Click += (_, _) => Suggest(); bar.Children.Add(_suggest); Grid.SetRow(bar, 2); Children.Add(bar);
        SetColumns();
        SizeChanged += (_, _) =>
        {
            var width = Math.Max(760, ActualWidth - 16); var fileWidth = Math.Clamp(width * .28, 260, 400); var episodeWidth = width - fileWidth - 220;
            if (Math.Abs(episodeWidth - _episodeWidth) < 1 && Math.Abs(fileWidth - _fileWidth) < 1) return;
            _fileWidth = fileWidth; _episodeWidth = episodeWidth; SetColumns(); Refresh();
        };
        Grid.SetRow(_local, 3); Children.Add(_local);
        var edit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; edit.Children.Add(_season); edit.Children.Add(_number);
        _apply.Click += (_, _) => ApplyNumbers(); edit.Children.Add(_apply);
        var hint = new TextBlock { Text = L.Text("Select an episode in the matching column. Adjust output numbering only when needed."), VerticalAlignment = VerticalAlignment.Bottom, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new(8, 0, 0, 5) }; edit.Children.Add(hint);
        Grid.SetRow(edit, 4); Children.Add(edit); Grid.SetRow(_summary, 5); Children.Add(_summary);
        _local.List.SelectionChanged += (_, _) => UpdateNumbers();
        Id(_local.List, "EpisodeLocalFiles"); Id(_apply, "EpisodeApplyNumbers"); Id(_order, "EpisodeOrder"); Id(_season, "EpisodeTargetSeason"); Id(_number, "EpisodeTargetNumber"); Id(_suggest, "EpisodeSuggest"); Id(_summary, "EpisodeComparisonSummary"); Id(_context, "EpisodeResultContext"); Id(_fileScope, "EpisodeFileScope"); Id(_sourceScope, "EpisodeSourceScope"); Refresh();
    }
    private void SetColumns() => _local.SetColumns(new[] { (L.Text("Local file"), _fileWidth), (L.Text("Output S / E"), 95d), (L.Text("Matched episode"), _episodeWidth), (L.Text("State"), 125d) });
    private void Merge(MetadataDocument document)
    {
        var key = EpisodeMatching.Key(document, _media.Kind);
        if (Documents.TryGetValue(key, out var old))
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<MetadataDocument>(System.Text.Json.JsonSerializer.Serialize(document))!;
            copy.Episodes = document.Episodes.Concat(old.Episodes).DistinctBy(e => e.Id).OrderBy(e => e.Season).ThenBy(e => e.Number).ToList(); Documents[key] = copy;
        }
        else Documents[key] = document;
    }
    public void Stage(MetadataMatch match, int? preferredFileGroup)
    {
        Merge(match.Document); SetContext(match.Document, preferredFileGroup is int s ? new[] { s } : Array.Empty<int>(), null);
        Suggest(automatic: true);
    }
    // Review one confirmed result without regenerating assignments.
    public bool ReviewExisting(int? focusSeason)
    {
        var match = focusSeason is int s ? Matches.GetValueOrDefault(s) : Matches.OrderBy(p => p.Key == 0 ? int.MaxValue : p.Key).FirstOrDefault().Value;
        if (match == null || match.Document.Episodes.Count == 0) return false;
        var key = EpisodeMatching.Key(match.Document, _media.Kind);
        var seasons = focusSeason is int local ? new[] { local } : Matches.Where(p => EpisodeMatching.Key(p.Value.Document, _media.Kind) == key).Select(p => p.Key).ToArray();
        SetContext(Documents.GetValueOrDefault(key) ?? match.Document, seasons, null); Refresh(); return true;
    }
    private void SetContext(MetadataDocument document, IEnumerable<int> localSeasons, int? remoteSeason)
    {
        _activeDocument = document; _settingContext = true;
        _fileScope.Items.Clear(); _fileScope.Items.Add(new ComboBoxItem { Content = L.Text("All local files"), Tag = -1 });
        var groups = _media.Files.Where(f => f.Exclusion.Length == 0 || f.NeedsReview || Bindings.ContainsKey(f.Path)).GroupBy(f => f.Season).OrderBy(g => g.Key).ToList();
        foreach (var group in groups) _fileScope.Items.Add(new ComboBoxItem { Content = group.All(f => f.Episode <= 0 && f.SourceNumber.Length == 0) ? L.Text("Unnumbered files") : L.Format($"Filename group S{group.Key:00} ({group.Count()} files)"), Tag = group.Key });
        var preferred = localSeasons.ToList();
        _fileScope.SelectedIndex = 0;
        if (preferred.Count == 1) _fileScope.SelectedItem = _fileScope.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int s && s == preferred[0]) ?? _fileScope.Items[0];
        _sourceScope.Items.Clear(); _sourceScope.Items.Add(new ComboBoxItem { Content = L.Text("All source episodes (including specials)"), Tag = -1 });
        foreach (var group in document.Episodes.GroupBy(e => e.Season).OrderBy(g => g.Key)) _sourceScope.Items.Add(new ComboBoxItem { Content = (group.Key == 0 ? L.Text("S00 / Specials") : document.Provider == "Bangumi" ? L.Text("Regular episodes in this subject") : $"S{group.Key:00}") + L.Format($" ({group.Count()} episodes)"), Tag = group.Key });
        _sourceScope.SelectedIndex = 0;
        if (remoteSeason is int remote) _sourceScope.SelectedItem = _sourceScope.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int s && s == remote) ?? _sourceScope.Items[0];
        _settingContext = false; UpdateScope();
    }
    private void UpdateScope()
    {
        if (_activeDocument == null) return;
        _localSeasons = _fileScope.SelectedItem is ComboBoxItem { Tag: int local } && local >= 0 ? new() { local } : _media.Files.Where(f => f.Exclusion.Length == 0 || f.NeedsReview || Bindings.ContainsKey(f.Path)).Select(f => f.Season).ToHashSet();
        _remoteSeason = _sourceScope.SelectedItem is ComboBoxItem { Tag: int remote } && remote >= 0 ? remote : _activeDocument.Provider == "Bangumi" ? 1 : null;
        _pickerOptions = new() { new(L.Text("Skip this file"), Skip: true) };
        var sourceFilter = _sourceScope.SelectedItem is ComboBoxItem { Tag: int source } && source >= 0 ? source : (int?)null;
        _pickerOptions.AddRange(EpisodeMatching.Choices(new[] { _activeDocument }, _media.Kind).Where(c => sourceFilter == null || c.Episode.Season == sourceFilter).Select(c => new PickerOption((c.Document.Provider == "Bangumi" && c.Episode.Season > 0 ? $"EP{c.Episode.Number:00}" : c.NumberLabel) + " · " + c.Episode.Title, c)));
        _context.Text = _activeDocument.Title + " · " + _activeDocument.Provider + " #" + _activeDocument.Id;
        _suggest.IsEnabled = EpisodeMatching.CanSuggestForResult(_activeDocument, _localSeasons);
        Refresh();
    }
    private IEnumerable<LocalMediaFile> VisibleFiles => _media.Files.Where(f => (_focusFile == null || f.Path == _focusFile) && (_focusFile != null || _localSeasons.Contains(f.Season) || f.Season == 0 && _activeDocument?.Episodes.Any(e => e.Season == 0) == true));
    private void Suggest(bool automatic = false)
    {
        if (_activeDocument == null || !_suggest.IsEnabled) { Refresh(); return; }
        var suggestions = EpisodeMatching.SuggestForResult(_media, _activeDocument, _localSeasons, _remoteSeason, _order.SelectedIndex == 1);
        // Fractional/multi-episode files are always reviewed manually; refreshed source numbering must not replace that decision.
        var key = EpisodeMatching.Key(_activeDocument, _media.Kind);
        foreach (var file in VisibleFiles.Where(f => f.Exclusion.Length == 0 && f.SourceNumber.Length == 0 && _localSeasons.Contains(f.Season) && !Excluded.Contains(f.Path)))
        {
            if (_remoteSeason is int remote && _localSeasons.Count > 1 && file.Season != remote) continue;
            if (automatic && Bindings.ContainsKey(file.Path)) continue;
            if (suggestions.TryGetValue(file.Path, out var b)) { Bindings[file.Path] = b; RecordMatch(b); }
            else if (!automatic && Bindings.GetValueOrDefault(file.Path)?.SourceKey == key) Bindings.Remove(file.Path);
        }
        Refresh();
    }
    private void RecordMatch(EpisodeBinding binding)
    {
        if (Documents.TryGetValue(binding.SourceKey, out var doc)) Matches[binding.Season] = new(new(doc.Provider, doc.Id, doc.Title, doc.OriginalTitle, doc.Year, doc.Overview, doc.PosterUrl), doc);
    }
    private FrameworkElement EpisodePicker(LocalMediaFile file)
    {
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360, PlaceholderText = L.Text("Choose matching episode"), Style = Ui.Style("CompactEpisodePickerStyle") };
        Id(picker, "EpisodePicker:" + file.Name); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.Text("Matched episode") + " · " + file.Name);
        var binding = Bindings.GetValueOrDefault(file.Path);
        picker.ItemsSource = _pickerOptions;
        picker.SelectedItem = Excluded.Contains(file.Path) ? _pickerOptions.FirstOrDefault(o => o.Skip) : _pickerOptions.FirstOrDefault(o => o.Choice is { } c && binding?.SourceKey == c.SourceKey && binding.Episode.Id == c.Episode.Id);
        if (!Excluded.Contains(file.Path) && binding != null && picker.SelectedItem == null)
        {
            var source = Documents.GetValueOrDefault(binding.SourceKey);
            var previous = new PickerOption(L.Text("Current match") + ": " + source?.Provider + " #" + source?.Id + " · " + binding.Episode.Title); picker.ItemsSource = _pickerOptions.Append(previous).ToList(); picker.SelectedItem = previous;
        }
        ToolTipService.SetToolTip(picker, picker.SelectedItem?.ToString() ?? L.Text("Choose matching episode"));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is PickerOption { Skip: true }) { Bindings.Remove(file.Path); Excluded.Add(file.Path); }
            else if (picker.SelectedItem is PickerOption { Choice: EpisodeChoice choice })
            {
                var targetSeason = choice.Episode.Season == 0 ? 0 : _localSeasons.Count == 1 && _remoteSeason == choice.Episode.Season ? _localSeasons.Single() : choice.Episode.Season;
                if (targetSeason == 0 && choice.Episode.Season > 0) targetSeason = choice.Episode.Season;
                Bindings[file.Path] = new(choice.SourceKey, choice.Episode, targetSeason, choice.Episode.Number); Excluded.Remove(file.Path);
                RecordMatch(Bindings[file.Path]);
            }
            else return;
            Refresh(); FocusFile(file.Path);
        };
        return picker;
    }
    private void UpdateNumbers()
    {
        var f = SelectedFile; var b = f == null ? null : Bindings.GetValueOrDefault(f.Path); _apply.IsEnabled = _season.IsEnabled = _number.IsEnabled = b != null;
        if (f != null) { _season.Value = b?.Season ?? f.Season; _number.Value = b?.Number ?? Math.Max(1, f.Episode); }
    }
    private void ApplyNumbers()
    {
        if (SelectedFile is not { } file || !Bindings.TryGetValue(file.Path, out var binding)) return;
        if (!double.IsFinite(_season.Value) || !double.IsFinite(_number.Value) || _season.Value != Math.Truncate(_season.Value) || _number.Value != Math.Truncate(_number.Value) || _season.Value < 0 || _season.Value > 999 || _number.Value < 1 || _number.Value > 9999) { _summary.Text = L.Text("Use integer season and episode numbers."); return; }
        Bindings[file.Path] = binding with { Season = (int)_season.Value, Number = (int)_number.Value }; RecordMatch(Bindings[file.Path]); Refresh();
    }
    public List<string> Conflicts => EpisodeMatching.Conflicts(Bindings.Where(p => !Excluded.Contains(p.Key)));
    public bool CanConfirm => HasResult && Bindings.Count > 0 && Conflicts.Count == 0;
    public void FocusFile(string? path)
    {
        if (path == null) return; _local.List.SelectedItem = (_local.List.ItemsSource as IEnumerable<TableRow>)?.FirstOrDefault(r => r.Tag is LocalMediaFile f && f.Path == path); if (_local.List.SelectedItem != null) _local.List.ScrollIntoView(_local.List.SelectedItem);
    }
    private void Refresh()
    {
        var selected = SelectedFile?.Path;
        _local.List.ItemsSource = VisibleFiles.OrderBy(f => Excluded.Contains(f.Path) ? 2 : f.NeedsReview && !Bindings.ContainsKey(f.Path) ? 0 : 1).ThenBy(f => f.Season).ThenBy(f => f.Episode).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var b = Bindings.GetValueOrDefault(f.Path);
            return new TableRow { Tag = f, Cells = new() { new(f.Name, _fileWidth), new(b == null ? f.NeedsReview ? f.SourceNumber.Length == 0 ? L.Text("Not assigned") : $"S{f.Season:00}E{f.SourceNumber}" : $"S{f.Season:00}E{f.Episode:00}" : $"S{b.Season:00}E{b.Number:00}", 95), new(b?.Episode.Title ?? "—", _episodeWidth) { Editor = () => EpisodePicker(f) }, new(L.Text(Excluded.Contains(f.Path) ? "Excluded" : b == null ? f.NeedsReview ? "Needs manual matching" : "Not matched" : "Matched"), 125) { Tone = Excluded.Contains(f.Path) ? CellTone.Muted : b != null ? CellTone.Success : f.NeedsReview ? CellTone.Warning : CellTone.Muted, Glyph = b != null ? "\uE73E" : f.NeedsReview ? "\uE7BA" : "" } } };
        }).ToList();
        FocusFile(selected); UpdateNumbers(); var conflicts = Conflicts;
        var visible = VisibleFiles.ToList();
        _summary.Text = L.Format($"This result: {visible.Count} files, {visible.Count(f => Bindings.ContainsKey(f.Path))} matched. Entire draft: {Bindings.Count} matched, {Excluded.Count} skipped, {conflicts.Count} conflicts.") + "\n" + (conflicts.Count > 0 ? string.Join("\n", conflicts.Take(3)) : L.Text(_activeDocument?.Provider == "Bangumi" && !_suggest.IsEnabled ? "This Bangumi result is one subject. Match files manually, or choose a filename group before suggesting by numbering. Existing assignments are retained." : "Filename groups are only hints. Match source episodes in the dropdown; output season and episode numbers can be adjusted below.")); Changed?.Invoke();
    }
    private static void Id(DependencyObject control, string id) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(control, id);
}
