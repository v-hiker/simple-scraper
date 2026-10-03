using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SimpleScraper.Views;

public sealed class SearchMatchDialog : ContentDialog
{
    private readonly LibraryMedia _media;
    private readonly ConfigService _config;
    private readonly Func<string, CancellationToken, IMetadataProvider> _providerFactory;
    private readonly TextBox _query = new();
    private static readonly string[] SourceNames = { "TMDB", "Bangumi" };
    private readonly Dictionary<string, ToggleMenuFlyoutItem> _sourceItems = new();
    private readonly DropDownButton _sources = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 };
    private readonly ListView _results = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly StackPanel _detail = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _search;
    private readonly SectionTabs _tabs = new();
    private SelectorBarItem? _comparisonTab;
    private readonly ProgressRing _loading = new() { Width = 22, Height = 22, IsActive = false };
    internal EpisodeComparisonPane? Comparison { get; }
    private MetadataMatch? _loadedMatch;
    private int _revision;
    private bool _closed;
    private CancellationTokenSource? _request;
    private readonly Dictionary<string, MetadataDocument> _loadedDocuments = new();
    private readonly Button _cancelRequest = new() { Content = L.Text("Cancel request"), Visibility = Visibility.Collapsed };
    public MetadataMatch? ConfirmedMatch { get; private set; }
    public bool NeedsTmdbConfiguration { get; private set; }
    public IReadOnlyList<string> SelectedSources => _sourceItems.Where(p => p.Value.IsChecked).Select(p => p.Key).ToArray();
    public string SearchTitle => _query.Text;

    public SearchMatchDialog(LibraryMedia media, ConfigService config, string? focusFile = null, bool openComparison = false, int? focusSeason = null, IReadOnlyList<string>? selectedSources = null, string? searchTitle = null, bool skipInitialSearch = false, Func<string, CancellationToken, IMetadataProvider>? providerFactory = null)
    {
        _media = media; _config = config;
        _providerFactory = providerFactory ?? App.Services.MetadataProvider;
        Ui.TrackTheme(this, config.GetUiTheme());
        Title = L.Text("Search and match"); PrimaryButtonText = L.Text("Confirm match"); CloseButtonText = L.Text("Cancel"); IsPrimaryButtonEnabled = false;
        Resources["ContentDialogMaxWidth"] = 1400d;
        Resources["ContentDialogMaxHeight"] = 1000d;
        var panel = new Grid { Width = 1200, Height = 680, RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) panel.RowDefinitions.Add(new() { Height = height });
        var location = T(media.Title + " · " + media.Folder, 13); location.TextWrapping = TextWrapping.NoWrap; location.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(location, media.Folder); panel.Children.Add(location);
        var sourceMenu = new MenuFlyout(); var defaults = selectedSources ?? config.GetDefaultMetadataSources();
        foreach (var name in SourceNames)
        {
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = defaults.Contains(name) }; item.Click += (_, _) => UpdateSources();
            _sourceItems[name] = item; sourceMenu.Items.Add(item); Id(item, "LibrarySource" + name);
        }
        _sources.Flyout = sourceMenu; UpdateSources(); ToolTipService.SetToolTip(_sources, L.Text("Metadata sources (multiple selection)"));
        var searchRow = new Grid { ColumnSpacing = 12 }; searchRow.ColumnDefinitions.Add(new() { Width = new(220) }); searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = new(140) });
        var sourceField = new StackPanel { Spacing = 8 }; sourceField.Children.Add(T(L.Text("Metadata sources (multiple selection)"))); sourceField.Children.Add(_sources); searchRow.Children.Add(sourceField);
        _query.Text = searchTitle ?? media.Metadata?.Title ?? media.Title; _query.Header = L.Text("Search title"); _query.PlaceholderText = L.Text("Metadata search keywords"); _query.MinHeight = 36; Grid.SetColumn(_query, 1); searchRow.Children.Add(_query);
        _search = new Button { Content = Ui.Label("\uE721", "Search metadata"), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom, MinHeight = 36 }; _search.Click += async (_, _) => await Search(); Grid.SetColumn(_search, 2); searchRow.Children.Add(_search);
        _query.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && _search.IsEnabled) { e.Handled = true; await Search(); } };
        Grid.SetRow(searchRow, 1); panel.Children.Add(searchRow);
        var focusedSeason = media.Files.FirstOrDefault(f => f.Path == focusFile)?.Season ?? focusSeason;
        var body = new Grid { ColumnSpacing = 16, MinHeight = 300 }; body.ColumnDefinitions.Add(new() { Width = new(1.2, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _results.ItemContainerStyle = Ui.Style("FluentResultItemStyle");
        body.Children.Add(Ui.Card(_results, new(4))); var detailsScroll = new ScrollViewer { Content = _detail, Padding = new(12) }; Grid.SetColumn(detailsScroll, 1); body.Children.Add(detailsScroll);
        _tabs.Add("Search results", "\uE721", body, "SearchResultsTab");
        if (media.Kind == LibraryMediaKind.Series) { Comparison = new(media, focusFile); _comparisonTab = _tabs.Add("Episode comparison", "\uE8AB", Comparison, "EpisodeComparisonTab", false); Comparison.Changed += UpdatePrimary; }
        _tabs.SelectionChanged += (_, _) => UpdatePrimary();
        Grid.SetRow(_tabs, 2); panel.Children.Add(_tabs);
        _detail.Children.Add(Ui.Empty("Select a search result", "Inspect the metadata here before confirming the correct result."));
        var direct = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var source = new ComboBox { ItemsSource = SourceNames, SelectedIndex = 0 }; var id = new TextBox { PlaceholderText = L.Text("Provider ID"), Width = 150 }; var lookup = new Button { Content = L.Text("Load metadata") };
        lookup.Click += async (_, _) => { if (_search.IsEnabled) await Load(new(source.SelectedItem.ToString()!, id.Text.Trim(), "", "", 0, "", "")); };
        direct.Children.Add(source); direct.Children.Add(id); direct.Children.Add(lookup);
        var advanced = new Expander { Header = L.Text("Direct ID lookup"), Content = direct, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(advanced, 3); panel.Children.Add(advanced);
        _tabs.SelectionChanged += (_, _) => { advanced.Visibility = searchRow.Visibility = _tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; };
        var feedback = new Grid { ColumnSpacing = 8 }; feedback.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); feedback.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); feedback.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); feedback.Children.Add(_loading); Grid.SetColumn(_status, 1); feedback.Children.Add(_status); Grid.SetColumn(_cancelRequest, 2); feedback.Children.Add(_cancelRequest); Grid.SetRow(feedback, 4); panel.Children.Add(feedback); Content = panel;
        _cancelRequest.Click += (_, _) => { _request?.Cancel(); ++_revision; _results.SelectedItem = null; _search.IsEnabled = true; _loading.IsActive = false; _cancelRequest.Visibility = Visibility.Collapsed; _status.Text = L.Text("Request cancelled. Select a result or search again."); UpdatePrimary(); }; Id(_cancelRequest, "LibraryCancelRequest");
        _results.SelectionChanged += async (_, _) => { if (_results.SelectedItem is ListViewItem { Tag: MetadataCandidate candidate }) await Load(candidate); };
        PrimaryButtonClick += (_, args) =>
        {
            if (Comparison == null) return;
            if (_tabs.SelectedIndex == 0)
            {
                args.Cancel = true;
                if (_loadedMatch == null) return;
                Comparison.Stage(_loadedMatch, focusedSeason); _loadedMatch = null; _comparisonTab!.IsEnabled = true;
                _status.Text = L.Text("Confirming saves matching records in the app. Scraping and renaming are separate actions.");
                _tabs.SelectedIndex = 1; Comparison.FocusFile(focusFile); UpdatePrimary();
            }
            else if (!Comparison.CanConfirm) { args.Cancel = true; _status.Text = L.Text("Resolve duplicate assignments before confirming."); }
        };
        Closed += (_, _) => { _closed = true; _revision++; _request?.Cancel(); };
        Loaded += (_, _) => { panel.Width = Math.Min(1200, Math.Max(620, XamlRoot.Size.Width - 100)); panel.Height = Math.Max(380, Math.Min(680, XamlRoot.Size.Height - 250)); };
        Opened += async (_, _) => { if (openComparison && Comparison != null && Comparison.ReviewExisting(focusedSeason)) { _comparisonTab!.IsEnabled = true; _tabs.SelectedIndex = 1; Comparison.FocusFile(focusFile); _status.Text = L.Text("Reviewing a saved search result. Choose another search result to match a different season."); UpdatePrimary(); } else if (!skipInitialSearch) await Search(); };
        Id(_query, "LibraryQuery"); Id(_sources, "LibrarySources"); Id(_results, "LibraryResults"); Id(_search, "LibrarySearch"); Id(id, "LibraryProviderId"); Id(source, "LibrarySource"); Id(lookup, "LibraryLookup");
    }
    private void UpdatePrimary()
    {
        PrimaryButtonText = Comparison == null ? L.Text("Confirm match") : L.Text(_tabs.SelectedIndex == 0 ? "Next: compare episodes" : "Save all episode matches");
        IsPrimaryButtonEnabled = !_loading.IsActive && (Comparison == null ? ConfirmedMatch != null : _tabs.SelectedIndex == 0 ? _loadedMatch != null : Comparison.CanConfirm);
    }
    private void UpdateSources() => _sources.Content = _sourceItems.Values.Any(i => i.IsChecked) ? string.Join(" + ", _sourceItems.Where(p => p.Value.IsChecked).Select(p => p.Key)) : L.Text("Choose search sources");
    private IMetadataProvider Provider(string source, CancellationToken token) => _providerFactory(source, token);
    private CancellationTokenSource StartRequest() { _request?.Cancel(); _request = new(); _cancelRequest.Visibility = Visibility.Visible; return _request; }
    private void FinishRequest(CancellationTokenSource request) { if (ReferenceEquals(_request, request)) { _request = null; _cancelRequest.Visibility = Visibility.Collapsed; } request.Dispose(); }
    private async Task Search()
    {
        if (RequestKeyConfiguration(SelectedSources)) return;
        if (_tabs.SelectedIndex != 0) _tabs.SelectedIndex = 0;
        if (_comparisonTab != null) _comparisonTab.IsEnabled = false;
        var revision = ++_revision; var request = StartRequest(); _loading.IsActive = true; IsPrimaryButtonEnabled = false; ConfirmedMatch = null; _loadedMatch = null; _results.Items.Clear(); _detail.Children.Clear(); _search.IsEnabled = false; _status.Text = L.Text("Searching…");
        try
        {
            var providers = _sourceItems.Where(p => p.Value.IsChecked).Select(p => Provider(p.Key, request.Token)).ToList();
            if (providers.Count == 0) throw new InvalidOperationException(L.Text("Choose at least one search source."));
            var query = _query.Text.Trim();
            var report = await Task.Run(() => MetadataProviderSearch.SearchAsync(providers, query, _media.Kind, request.Token));
            if (_closed || revision != _revision) return;
            foreach (var c in report.Candidates)
            {
                var row = new StackPanel { Spacing = 5 }; var title = T(c.Title + (c.Year > 0 ? $" ({c.Year})" : "")); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; row.Children.Add(title); var source = T(c.Provider + " #" + c.Id + " · " + c.OriginalTitle, 12); source.Style = Ui.Style("SecondaryTextStyle"); row.Children.Add(source);
                var item = new ListViewItem { Tag = c, Content = row };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, c.ToString());
                _results.Items.Add(item);
            }
            _status.Text = L.Format($"Found {report.Candidates.Count} candidates. Select one and confirm the match.") + "\n" + string.Join("\n", report.Errors);
            _detail.Children.Add(Ui.Empty(report.Candidates.Count == 0 ? "No matching titles" : "Select a search result", report.Candidates.Count == 0 ? "Try another title or metadata source." : "Inspect the metadata here before confirming the correct result."));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e) { if (!_closed && revision == _revision) _status.Text = e.Message; }
        finally { FinishRequest(request); if (!_closed && revision == _revision) { _search.IsEnabled = true; _loading.IsActive = false; UpdatePrimary(); } }
    }
    private async Task Load(MetadataCandidate candidate)
    {
        if (!_loadedDocuments.ContainsKey(candidate.Provider + "/" + candidate.Id) && RequestKeyConfiguration(new[] { candidate.Provider })) return;
        if (_comparisonTab != null) _comparisonTab.IsEnabled = false;
        var revision = ++_revision; var request = StartRequest();
        _loading.IsActive = true; IsPrimaryButtonEnabled = false; ConfirmedMatch = null; _loadedMatch = null; _detail.Children.Clear(); _detail.Children.Add(T(candidate.ToString(), 18)); _detail.Children.Add(Ui.Overview(candidate.Overview)); _status.Text = L.Text("Loading metadata…");
        try
        {
            await Task.Delay(150, request.Token);
            var key = candidate.Provider + "/" + candidate.Id;
            var provider = Provider(candidate.Provider, request.Token);
            var doc = _loadedDocuments.GetValueOrDefault(key) ?? await Task.Run(() => provider.LoadAsync(candidate.Id, _media.Kind, Array.Empty<int>()));
            if (_closed || revision != _revision) return;
            _loadedDocuments[key] = doc;
            _detail.Children.Clear();
            if (doc.PosterUrl.Length > 0) _detail.Children.Add(new Image { Height = 175, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform, Source = new BitmapImage(new Uri(doc.PosterUrl)) { DecodePixelWidth = 240 } });
            _detail.Children.Add(T(doc.Title, 20)); _detail.Children.Add(T(doc.OriginalTitle + "\n" + doc.Date + $" · ★ {doc.Rating:0.0}")); _detail.Children.Add(Ui.SourceLinks(doc, _media.Kind)); _detail.Children.Add(Ui.Overview(doc.Overview, doc.OriginalOverview, currentLanguage: doc.OverviewLanguage));
            ConfirmedMatch = new(new(candidate.Provider, doc.Id, doc.Title, doc.OriginalTitle, doc.Year, doc.Overview, doc.PosterUrl), doc); _loadedMatch = ConfirmedMatch;
            _status.Text = _media.Kind == LibraryMediaKind.Series ? L.Format($"Loaded {doc.Episodes.Count} source episodes. Local numbering is only a reference; confirm assignments in episode comparison.") : L.Text("Confirming saves matching records in the app. Scraping and renaming are separate actions."); IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e) { if (!_closed && revision == _revision) _status.Text = candidate.Provider + ": " + e.Message; }
        finally { FinishRequest(request); if (!_closed && revision == _revision) { _search.IsEnabled = true; _loading.IsActive = false; UpdatePrimary(); } }
    }
    private bool RequestKeyConfiguration(IEnumerable<string> sources)
    {
        if (!TmdbCredentials.NeedsConfiguration(sources, _config.GetApiKey())) return false;
        // Close this dialog before opening the setup prompt: WinUI allows one ContentDialog at a time.
        NeedsTmdbConfiguration = true; Hide(); return true;
    }
    private static TextBlock T(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static void Id(DependencyObject element, string id) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);
}
