using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace SimpleScraper.Views;

internal sealed class MetadataEditorDialog : ContentDialog
{
    private readonly MetadataDocument _draft;
    private readonly ConfigService _config;
    private readonly TextBox _title, _originalTitle, _date, _tagline, _genres, _studios, _directors, _writers;
    private readonly TextBox _overview, _originalOverview, _poster, _backdrop, _logo;
    private readonly NumberBox _rating, _votes, _runtime;
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, TextBox> _ids = new();
    private readonly ObservableCollection<TableRow> _actors = new();
    private sealed class ActorEdit(CastMember actor)
    {
        public CastMember Original { get; } = actor;
        public string Name { get; set; } = actor.Name;
        public string Role { get; set; } = actor.Character;
        public string Photo { get; set; } = actor.ProfilePath ?? "";
    }
    private readonly Dictionary<TableRow, ActorEdit> _actorFields = new();
    public MetadataDocument? Edited { get; private set; }

    public MetadataEditorDialog(MetadataDocument document, ConfigService config)
    {
        _draft = MetadataEditing.Copy(document); _config = config;
        Title = L.Text("Edit metadata"); PrimaryButtonText = L.Text("Save"); CloseButtonText = L.Text("Cancel");
        Resources["ContentDialogMaxWidth"] = 960d;
        var tabs = new SectionTabs { Width = 840, Height = 530 };
        var basics = new StackPanel { Spacing = 12 };
        _title = Input("Title", _draft.Title, "MetadataEditTitle"); _originalTitle = Input("Original title", _draft.OriginalTitle);
        basics.Children.Add(_title); basics.Children.Add(_originalTitle);
        _date = Input("Date", _draft.Date, "MetadataEditDate"); _date.PlaceholderText = "yyyy-MM-dd";
        _rating = Number("Rating", _draft.Rating, 10, "MetadataEditRating"); _votes = Number("Votes", _draft.VoteCount, int.MaxValue); _runtime = Number("Runtime (minutes)", _draft.Runtime, int.MaxValue);
        basics.Children.Add(Row(_date, _rating, _votes, _runtime));
        _tagline = Input("Tagline", _draft.Tagline); basics.Children.Add(_tagline);
        _genres = Input("Genres", string.Join("\n", _draft.Genres), multiline: true);
        _studios = Input("Studios", string.Join("\n", _draft.Studios), multiline: true); basics.Children.Add(Row(_genres, _studios));
        tabs.Add("Metadata", "\uE8A5", Scroll(basics), "MetadataEditorBasic");

        var descriptions = OverviewText.Split(_draft.Overview, _draft.OriginalOverview);
        var target = SynopsisTranslationService.Target(config.GetScrapeLanguage());
        var known = _draft.OverviewLanguage.Length > 0 ? _draft.OverviewLanguage : SynopsisTranslationService.KnownLanguage(descriptions.Current);
        if (known.Length > 0 && !known.Split('-')[0].Equals(target.Split('-')[0], StringComparison.OrdinalIgnoreCase)) descriptions = new("", descriptions.Original.Length > 0 ? descriptions.Original : descriptions.Current);
        _overview = Input("Current language", descriptions.Current, "MetadataEditOverview", true);
        _originalOverview = Input("Original text", descriptions.Original, "MetadataEditOriginalOverview", true);
        foreach (var box in new[] { _overview, _originalOverview }) { box.Height = 335; box.MaxHeight = 335; ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto); }
        var overviewPanel = new StackPanel { Spacing = 12 };
        var selector = new SelectorBar(); var current = new SelectorBarItem { Text = L.Text("Current language") }; var original = new SelectorBarItem { Text = L.Text("Original text") }; selector.Items.Add(current); selector.Items.Add(original); selector.SelectedItem = current;
        _overview.Header = _originalOverview.Header = null; _originalOverview.Visibility = Visibility.Collapsed;
        selector.SelectionChanged += (_, _) => { _overview.Visibility = selector.SelectedItem == current ? Visibility.Visible : Visibility.Collapsed; _originalOverview.Visibility = selector.SelectedItem == original ? Visibility.Visible : Visibility.Collapsed; };
        var settings = config.GetTranslationSettings(); var sources = new ComboBox();
        Id(sources, "MetadataEditTranslationSource");
        foreach (var option in TranslationProviders.Options) { var item = new ComboBoxItem { Content = L.Text(option.Name), Tag = option.Id }; sources.Items.Add(item); if (option.Id == settings.Provider) sources.SelectedItem = item; }
        var translate = new Button { Content = Ui.Label("\uE8D2", "Translate") }; Id(translate, "MetadataEditTranslate");
        var loading = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
        var machine = new TextBlock { Style = Ui.Style("SecondaryTextStyle"), FontSize = 12 };
        var actions = new Grid { ColumnSpacing = 8 }; actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.Children.Add(sources); Grid.SetColumn(translate, 1); actions.Children.Add(translate); Grid.SetColumn(loading, 2); actions.Children.Add(loading);
        translate.Click += async (_, _) =>
        {
            var text = _originalOverview.Text.Length > 0 ? _originalOverview.Text : _overview.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            var token = _cancellation.Token; _error.IsOpen = false; IsPrimaryButtonEnabled = translate.IsEnabled = sources.IsEnabled = _overview.IsEnabled = _originalOverview.IsEnabled = false; loading.IsActive = true; loading.Visibility = Visibility.Visible;
            try
            {
                var provider = (sources.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? settings.Provider;
                var translator = new SynopsisTranslationService(provider: TranslationProviders.Create(_config.GetTranslationSettings() with { Provider = provider }));
                var result = await translator.TranslateAsync(text, target, token); token.ThrowIfCancellationRequested();
                if (_originalOverview.Text.Length == 0) _originalOverview.Text = text;
                _overview.Text = result; selector.SelectedItem = current; _draft.OverviewLanguage = target;
                machine.Text = L.Text("Machine translation") + " · " + translator.ProviderName;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { if (!token.IsCancellationRequested) { _error.Message = exception is InvalidDataException or InvalidOperationException ? exception.Message : L.Text("Translation failed. Check the connection or try again later."); _error.IsOpen = true; } }
            finally { if (!token.IsCancellationRequested) IsPrimaryButtonEnabled = translate.IsEnabled = sources.IsEnabled = _overview.IsEnabled = _originalOverview.IsEnabled = true; loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
        };
        overviewPanel.Children.Add(selector); overviewPanel.Children.Add(_overview); overviewPanel.Children.Add(_originalOverview); overviewPanel.Children.Add(actions); overviewPanel.Children.Add(machine);
        tabs.Add("Overview", "\uE8D2", Scroll(overviewPanel), "MetadataEditorOverview");

        var credits = new StackPanel { Spacing = 12 };
        _directors = Input("Directors", string.Join("\n", _draft.Crew.Where(c => IsDirector(c)).Select(c => c.Name)), multiline: true);
        _writers = Input("Writers", string.Join("\n", _draft.Crew.Where(c => IsWriter(c)).Select(c => c.Name)), multiline: true); credits.Children.Add(Row(_directors, _writers));
        var table = new DenseTable { Height = 310 }; table.SetColumns(new[] { (L.Text("Name"), 200d), (L.Text("Role"), 190d), (L.Text("Photo URL"), 320d), ("", 44d) }); table.List.SelectionMode = ListViewSelectionMode.None; table.List.ItemsSource = _actors;
        foreach (var actor in _draft.Cast) AddActor(actor);
        var add = new Button { Content = Ui.Label("\uE710", "Add actor") }; add.Click += (_, _) => AddActor(new CastMember()); credits.Children.Add(add); credits.Children.Add(table);
        tabs.Add("Actors", "\uE716", Scroll(credits), "MetadataEditorCredits");
        var artwork = new StackPanel { Spacing = 12 }; _poster = Input("Poster / episode still URL", _draft.PosterUrl); _backdrop = Input("Backdrop URL", _draft.BackdropUrl); _logo = Input("Clearlogo URL", _draft.ClearLogoUrl); artwork.Children.Add(_poster); artwork.Children.Add(_backdrop); artwork.Children.Add(_logo);
        foreach (var provider in new[] { "tmdb", "bangumi", "imdb", "tvdb" })
        {
            var field = Input(provider.Equals("bangumi") ? "Bangumi ID" : provider.ToUpperInvariant() + " ID", _draft.Ids.GetValueOrDefault(provider, _draft.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) ? _draft.Id : "")); _ids[provider] = field; artwork.Children.Add(field);
        }
        tabs.Add("Artwork & IDs", "\uE91B", Scroll(artwork), "MetadataEditorArtwork");
        var host = new StackPanel { Spacing = 10 }; host.Children.Add(tabs); host.Children.Add(_error); Content = host;
        PrimaryButtonClick += (_, args) =>
        {
            try { Edited = ReadDraft(); }
            catch (Exception exception) { args.Cancel = true; _error.Message = exception.Message; _error.IsOpen = true; }
        };
        Closed += (_, _) => { _cancellation.Cancel(); };
    }
    private void AddActor(CastMember actor)
    {
        var fields = new ActorEdit(actor);
        var row = new TableRow { Cells = new() { new("", 200) { Editor = () => ActorInput(fields.Name, value => fields.Name = value) }, new("", 190) { Editor = () => ActorInput(fields.Role, value => fields.Role = value) }, new("", 320) { Editor = () => ActorInput(fields.Photo, value => fields.Photo = value) }, new("", 44) } };
        row.Cells[3] = new("", 44) { Editor = () => { var remove = new Button { Content = Ui.Icon("\uE74D", 14), Style = Ui.Style("SubtleButtonStyle"), Padding = new(6) }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L.Text("Remove actor")); remove.Click += (_, _) => { _actors.Remove(row); _actorFields.Remove(row); }; return remove; } };
        _actorFields[row] = fields; _actors.Add(row);
    }
    private static TextBox ActorInput(string value, Action<string> changed) { var box = Input("", value); box.TextChanged += (_, _) => changed(box.Text); return box; }
    private MetadataDocument ReadDraft()
    {
        _draft.Title = _title.Text.Trim(); _draft.OriginalTitle = _originalTitle.Text.Trim(); _draft.Date = _date.Text.Trim(); _draft.Tagline = _tagline.Text.Trim();
        _draft.Rating = _rating.Value; _draft.VoteCount = Integer(_votes.Value); _draft.Runtime = Integer(_runtime.Value);
        _draft.Overview = _overview.Text; _draft.OriginalOverview = _originalOverview.Text; _draft.OriginalOverviewLoaded = _originalOverview.Text.Length > 0;
        _draft.OverviewLanguage = SynopsisTranslationService.KnownLanguage(_overview.Text);
        _draft.Genres = Lines(_genres.Text); _draft.Studios = Lines(_studios.Text);
        var others = _draft.Crew.Where(c => !IsDirector(c) && !IsWriter(c)).ToList();
        foreach (var name in Lines(_directors.Text)) others.Add(_draft.Crew.FirstOrDefault(c => IsDirector(c) && c.Name == name) ?? new() { Name = name, Job = "Director" });
        foreach (var name in Lines(_writers.Text)) others.Add(_draft.Crew.FirstOrDefault(c => IsWriter(c) && c.Name == name) ?? new() { Name = name, Job = "Writer", Department = "Writing" }); _draft.Crew = others;
        _draft.Cast = _actors.Select((row, index) => { var fields = _actorFields[row]; return new CastMember { Id = fields.Name.Trim() == fields.Original.Name ? fields.Original.Id : 0, Name = fields.Name.Trim(), Character = fields.Role.Trim(), ProfilePath = fields.Photo.Trim(), Order = index }; }).Where(a => a.Name.Length > 0).ToList();
        _draft.PosterUrl = _poster.Text.Trim(); _draft.BackdropUrl = _backdrop.Text.Trim(); _draft.ClearLogoUrl = _logo.Text.Trim();
        foreach (var field in _ids) { if (field.Value.Text.Trim().Length == 0) _draft.Ids.Remove(field.Key); else _draft.Ids[field.Key] = field.Value.Text.Trim(); }
        if (_ids.ContainsKey(_draft.Provider.ToLowerInvariant())) _draft.Id = _draft.Ids.GetValueOrDefault(_draft.Provider.ToLowerInvariant(), "");
        MetadataEditing.Validate(_draft); return _draft;
    }
    private static int Integer(double value) { if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || Math.Truncate(value) != value) throw new ArgumentException(L.Text("Use non-negative numbers.")); return (int)value; }
    private static bool IsDirector(CrewMember c) => c.Job is "Director" or "导演" or "总导演" or "監督";
    private static bool IsWriter(CrewMember c) => c.Department == "Writing" || c.Job is "Writer" or "Screenplay" or "Creator" or "脚本" or "系列构成";
    private static List<string> Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
    private static TextBox Input(string label, string value, string id = "", bool multiline = false)
    {
        // WinUI's single-line text engine truncates at the first line break.
        // Enable multiline mode before assigning text, for every editable field.
        var box = new TextBox { Header = label.Length > 0 ? L.Text(label) : null, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 80 : 0 };
        if (multiline)
        {
            ScrollViewer.SetVerticalScrollMode(box, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollMode(box, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);
        }
        box.Text = value;
        Id(box, id); return box;
    }
    private static NumberBox Number(string label, double value, double maximum, string id = "") { var box = new NumberBox { Header = L.Text(label), Value = value, Minimum = 0, Maximum = maximum, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; Id(box, id); return box; }
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, Padding = new(0, 12, 12, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static Grid Row(params FrameworkElement[] children) { var grid = new Grid { ColumnSpacing = 12 }; for (var i = 0; i < children.Length; i++) { grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); } return grid; }
    private static void Id(DependencyObject element, string id) { if (id.Length > 0) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id); }
}
