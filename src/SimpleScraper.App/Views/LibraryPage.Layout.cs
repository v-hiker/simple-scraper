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
    private void Build()
    {
        Host.RowDefinitions.Add(new() { Height = GridLength.Auto }); Host.RowDefinitions.Add(new() { Height = new(3) }); Host.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); Host.RowDefinitions.Add(new() { Height = new(56) });
        // The card's border and padding precede the toolbar grid. Subtract
        // them from its sidebar track so the first command shares the media
        // card's outer left edge, including when the details pane is hidden.
        var toolbar = new Grid { ColumnSpacing = WorkspaceColumnGap }; toolbar.ColumnDefinitions.Add(new() { Width = new(SidebarWidth - ToolbarInset - CardBorder) }); toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(new TextBlock { Text = "简单刮削器", FontSize = 21 }); var caption = Text("Movie & TV metadata", 12); caption.Style = Ui.Style("SecondaryTextStyle"); brand.Children.Add(caption); toolbar.Children.Add(brand);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var refresh = Command("Refresh library", "\uE72C", Scan, "LibraryRefresh", 20, 143, 193); commands.Children.Add(refresh); _writeActions.Add(refresh);
        var search = Command("Search & scrape", "\uE721", null, "LibrarySearchToolbar", 116, 82, 216, true);
        search.Style = Ui.Style("AccentButtonStyle"); var searchIcon = (FontIcon)((StackPanel)search.Content).Children[0]; searchIcon.ClearValue(IconElement.ForegroundProperty); searchIcon.Style = Ui.Style("OnAccentIconStyle");
        var searchMenu = new MenuFlyout(); searchMenu.Items.Add(Menu("Search and match", "\uE721", Search, "LibrarySearch")); searchMenu.Items.Add(Menu("Scrape selected", "\uE896", Scrape, "LibraryScrape")); searchMenu.Items.Add(Menu("Compare episode matches", "\uE8AB", CompareEpisodes)); searchMenu.Items.Add(new MenuFlyoutSeparator()); searchMenu.Items.Add(Menu("List missing episodes", "\uE8A5", MissingEpisodes)); searchMenu.Items.Add(Menu("Update metadata", "\uE74E", UpdateMetadata, "LibraryUpdateMetadata")); search.Flyout = searchMenu; commands.Children.Add(search); _selectionActions.Add(search);
        var edit = Command("Edit metadata", "\uE70F", () => Run(EditMetadata), "LibraryEditMetadata", 16, 153, 103); _episodeEdit = edit; commands.Children.Add(edit); _selectionActions.Add(edit);
        var rename = Command("Rename & organize", "\uE8AC", () => Run(PreviewRename), "LibraryRename", 217, 132, 32); commands.Children.Add(rename); _selectionActions.Add(rename);
        searchMenu.Opening += (_, _) => { searchMenu.Items[2].IsEnabled = searchMenu.Items[4].IsEnabled = Current?.Kind == LibraryMediaKind.Series; };
        var commandScroll = new ScrollViewer { Content = commands, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(commandScroll, 1); toolbar.Children.Add(commandScroll);
        var utilities = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var paneToggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = Ui.Label("\uE8A5", "Details pane"), IsChecked = _config.GetDetailsPaneVisible(), MinHeight = 40, Padding = new(12, 8, 12, 8) }; Id(paneToggle, "LibraryDetailsPane"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(paneToggle, L.Text("Details pane")); utilities.Children.Add(paneToggle);
        var settings = Command("Settings", "\uE713", OpenSettings, "LibrarySettings", 110, 110, 110); utilities.Children.Add(settings); Grid.SetColumn(utilities, 2); toolbar.Children.Add(utilities);
        var toolbarCard = Ui.Card(toolbar, new(ToolbarInset, 12, ToolbarInset, 12)); toolbarCard.BorderThickness = new(CardBorder); toolbarCard.Margin = new(WorkspaceMargin, 8, WorkspaceMargin, 4); Host.Children.Add(toolbarCard);
        Grid.SetRow(_progress, 1); Host.Children.Add(_progress);
        var workspace = new Grid { ColumnSpacing = WorkspaceColumnGap, Margin = new(WorkspaceMargin, 8, WorkspaceMargin, 8) }; workspace.ColumnDefinitions.Add(new() { Width = new(SidebarWidth) }); workspace.ColumnDefinitions.Add(new() { Width = new(1.6, GridUnitType.Star), MinWidth = 350 }); workspace.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 340 }); Grid.SetRow(workspace, 2); Host.Children.Add(workspace);
        var left = new Grid { RowSpacing = 10 }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var libraryHeading = new StackPanel { Spacing = 12 }; var libraryTitle = Text("Media library", 14); libraryTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; libraryTitle.Margin = new(8, 0, 0, 0); libraryHeading.Children.Add(libraryTitle);
        var add = Button("Add library", AddLibrary, "LibraryAdd"); add.Content = Ui.Label("\uE710", "Add library"); add.HorizontalAlignment = HorizontalAlignment.Stretch; libraryHeading.Children.Add(add); left.Children.Add(libraryHeading); _writeActions.Add(add);
        _libraries.ItemTemplate = (DataTemplate)Application.Current.Resources["LibraryRootTemplate"]; _libraries.ItemContainerStyle = Ui.Style("FluentNavigationItemStyle"); _libraries.RightTapped += (_, e) => Ui.SelectRow(e.OriginalSource as DependencyObject); Grid.SetRow(_libraries, 1); left.Children.Add(_libraries);
        var librarySettings = Button("Library settings", ConfigureLibrary, "LibraryConfigure"); librarySettings.Content = Ui.Label("\uE713", "Library settings"); librarySettings.Style = Ui.Style("SubtleButtonStyle"); Grid.SetRow(librarySettings, 2); left.Children.Add(librarySettings); _writeActions.Add(librarySettings); workspace.Children.Add(left);
        var libraryMenu = new MenuFlyout(); libraryMenu.Items.Add(Menu("Library settings", "\uE713", ConfigureLibrary)); _libraries.ContextFlyout = libraryMenu;
        var middle = new Grid { RowSpacing = 8 }; middle.RowDefinitions.Add(new() { Height = GridLength.Auto }); middle.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var filters = new Grid { ColumnSpacing = 8, Padding = new(12) }; filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _filter.PlaceholderText = L.Text("Filter by title or year"); ToolTipService.SetToolTip(_filter, L.Text("Filter this library (Ctrl+F)")); filters.Children.Add(_filter);
        _filterState.ItemsSource = new[] { L.Text("All media"), L.Text("Not matched"), L.Text("Missing NFO"), L.Text("Missing poster"), L.Text("Needs attention") }; _filterState.SelectedIndex = 0; Grid.SetColumn(_filterState, 1); filters.Children.Add(_filterState);
        var columns = Button("Columns", ConfigureColumns, "LibraryColumns"); columns.Content = Ui.Label("\uE8A9", "Columns"); Grid.SetColumn(columns, 2); filters.Children.Add(columns); middle.Children.Add(filters); Grid.SetRow(_table, 1); middle.Children.Add(_table); var mediaCard = Ui.Card(middle, new(0)); Grid.SetColumn(mediaCard, 1); workspace.Children.Add(mediaCard);
        _table.SetEmptyState("No media to display", "Add a library or change the filter to see your media."); _cast.SetEmptyState("No cast information", "Cast information appears after matching a source that provides it.");
        _table.RightTapped += async (_, e) => { if (e.OriginalSource is Button && !_busy) await ConfigureColumns(); };
        _cast.SetColumns(new[] { (L.Text("Photo"), 64d), (L.Text("Actors"), 220d), (L.Text("Role"), 260d) });
        foreach (var tab in new[] { ("Metadata", "\uE8A5", (UIElement)new ScrollViewer { Content = _details }), ("Actors", "\uE716", (UIElement)_cast), ("Artwork", "\uE91B", (UIElement)new ScrollViewer { Content = _art }) }) _tabs.Add(tab.Item1, tab.Item2, tab.Item3, "DetailsTab" + tab.Item1);
        var right = Ui.Card(_tabs, new(12)); Grid.SetColumn(right, 2); workspace.Children.Add(right);
        void SetDetailsVisibility() { var visible = paneToggle.IsChecked == true; right.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; workspace.ColumnDefinitions[2].MinWidth = visible ? 340 : 0; workspace.ColumnDefinitions[2].Width = visible ? new(1, GridUnitType.Star) : new(0); }
        paneToggle.Checked += (_, _) => { SetDetailsVisibility(); _config.SetDetailsPaneVisible(true); }; paneToggle.Unchecked += (_, _) => { SetDetailsVisibility(); _config.SetDetailsPaneVisible(false); }; SetDetailsVisibility(); ToolTipService.SetToolTip(paneToggle, L.Text("Show or hide metadata to give the media list more space."));
        var footer = new Grid { Height = 36, ColumnSpacing = 10, Margin = new(16, 8, 16, 12) }; footer.ColumnDefinitions.Add(new() { Width = new(22) }); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(_spinner); Grid.SetColumn(_status, 1); footer.Children.Add(_status); Grid.SetColumn(_counts, 2); footer.Children.Add(_counts); Grid.SetColumn(_cancel, 3); footer.Children.Add(_cancel); Grid.SetRow(footer, 3); Host.Children.Add(footer);
        Id(_status, "LibraryStatus"); Id(footer, "LibraryStatusBar");
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(_status, new TextBlock { Text = _status.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }));
        _cancel.Click += (_, _) => _scanCancellation?.Cancel(); _status.Text = L.Text("Add a library, select a movie or series, then search → confirm → scrape → optionally rename."); Id(Items, "LibraryItems"); Id(_libraries, "LibraryRoots"); Id(_spinner, "LibraryLoading"); Id(_filter, "LibraryFilter"); UpdateActions();
        Items.KeyDown += async (_, e) => { if (_busy || Selected is not { File: null } selection || selection.Media.Kind != LibraryMediaKind.Series) return; if (e.Key == Windows.System.VirtualKey.Right && !_expanded.Contains(selection.Key) || e.Key == Windows.System.VirtualKey.Left && _expanded.Contains(selection.Key)) { e.Handled = true; await ToggleRow(selection); } };
    }

    private Button Command(string text, string glyph, Func<Task>? action, string id, byte red, byte green, byte blue, bool dropdown = false)
    {
        var content = Ui.Label(glyph, text); ((FontIcon)content.Children[0]).Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, red, green, blue));
        if (dropdown) content.Children.Add(Ui.Icon("\uE70D", 10));
        var button = new Button { Content = content, MinHeight = 40, Padding = new(14, 8, 14, 8), CornerRadius = new(6), VerticalAlignment = VerticalAlignment.Center }; Id(button, id); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.Text(text)); ToolTipService.SetToolTip(button, L.Text(text)); if (action != null) button.Click += async (_, _) => { if (!_busy) try { await action(); } catch (Exception e) { _status.Text = e.Message; } }; return button;
    }

    private MenuFlyoutItem Menu(string text, string glyph, Func<Task> action, string id = "")
    {
        var item = new MenuFlyoutItem { Text = L.Text(text), Icon = Ui.Icon(glyph) }; Id(item, id); item.Click += async (_, _) => { await Task.Delay(100); await Run(action); }; return item;
    }

    private Button Button(string text, Func<Task> action, string id = "") { var button = new Button { Content = L.Text(text) }; Id(button, id); button.Click += async (_, _) => { if (!_busy) try { await action(); } catch (Exception e) { _status.Text = e.Message; } }; return button; }

    private static TextBlock Text(string text, double size = 14) => new() { Text = L.Text(text), FontSize = size, TextWrapping = TextWrapping.Wrap };

    private static void Id(DependencyObject element, string id) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);

    private static Grid InlineField(string label, Control control)
    {
        var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new() { Width = new(100) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var text = Text(label); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
        control.HorizontalAlignment = HorizontalAlignment.Stretch; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, L.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
}
