using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;

namespace SimpleScraper.Views;

internal static class Ui
{
    private static readonly List<WeakReference<ContentDialog>> Dialogs = new();
    public static ElementTheme Theme(string theme) => theme == "dark" ? ElementTheme.Dark : theme == "light" ? ElementTheme.Light : new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128 ? ElementTheme.Dark : ElementTheme.Light;
    public static void TrackTheme(ContentDialog dialog, string theme)
    {
        dialog.RequestedTheme = Theme(theme); var reference = new WeakReference<ContentDialog>(dialog); Dialogs.Add(reference); dialog.Closed += (_, _) => Dialogs.Remove(reference);
    }
    public static void ApplyTheme(string theme) { var value = Theme(theme); if (App.MainWindow.Content is FrameworkElement root) root.RequestedTheme = value; foreach (var reference in Dialogs.ToArray()) if (reference.TryGetTarget(out var dialog)) dialog.RequestedTheme = value; else Dialogs.Remove(reference); }
    public static async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        if (!Dialogs.Any(reference => reference.TryGetTarget(out var tracked) && ReferenceEquals(tracked, dialog))) TrackTheme(dialog, App.Services.Config.GetUiTheme());
        return await dialog.ShowAsync();
    }
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static Style? ToneStyle(CellTone tone) => tone == CellTone.None ? null : Style(tone switch { CellTone.Success => "SuccessTextStyle", CellTone.Warning => "WarningTextStyle", CellTone.Critical => "CriticalTextStyle", CellTone.Info => "InfoTextStyle", _ => "SecondaryTextStyle" });
    public static Border Card(UIElement child, Thickness? padding = null) => new() { Style = Style("SurfaceCardStyle"), Child = child, Padding = padding ?? new Thickness(12) };
    public static StackPanel Empty(string title, string message)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 36, Opacity = .55 });
        panel.Children.Add(new TextBlock { Text = L.Text(title), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.Text(message), FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Style = Style("SecondaryTextStyle") }); return panel;
    }
    public static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T found) return found; if (FindChild<T>(child) is { } nested) return nested; } return null;
    }
    public static void SelectRow(DependencyObject? source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node)) if (node is ListViewItem item && ItemsControl.ItemsControlFromItemContainer(item) is ListView list) { list.SelectedItem = item.Content; return; }
    }
    public static Brush Brush(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Windows.UI.Color.FromArgb(18, 128, 128, 128));
    public static void Enter(UIElement element)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element); var animation = visual.Compositor.CreateScalarKeyFrameAnimation(); animation.InsertKeyFrame(0, 0); animation.InsertKeyFrame(1, 1); animation.Duration = TimeSpan.FromMilliseconds(160); visual.StartAnimation("Opacity", animation);
    }
    public static FontIcon Icon(string glyph, double size = 18) => new() { Glyph = glyph, FontSize = size };
    public static StackPanel Overview(string text, string original = "", Func<Task<string>>? loadOriginal = null, string currentLanguage = "")
    {
        var versions = Services.OverviewText.Split(text, original);
        var targetLanguage = Services.SynopsisTranslationService.Target(App.Services.Config.GetScrapeLanguage());
        var knownLanguage = currentLanguage.Length > 0 ? currentLanguage : versions.Original.Length == 0 ? Services.SynopsisTranslationService.KnownLanguage(versions.Current) : "";
        if (knownLanguage.Length > 0 && !knownLanguage.Split('-')[0].Equals(targetLanguage.Split('-')[0], StringComparison.OrdinalIgnoreCase)) versions = new("", versions.Original.Length > 0 ? versions.Original : versions.Current);
        var panel = new StackPanel { Spacing = 8 };
        if (versions.Current.Length == 0 && versions.Original.Length == 0 && loadOriginal == null) return panel;
        var selector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        var current = new SelectorBarItem { Text = L.Text("Current language"), IsEnabled = versions.Current.Length > 0 };
        var raw = new SelectorBarItem { Text = L.Text("Original text"), IsEnabled = versions.Original.Length > 0 || loadOriginal != null };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(current, "SynopsisCurrentLanguage");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(raw, "SynopsisOriginalLanguage");
        selector.Items.Add(current); selector.Items.Add(raw);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(body, "SynopsisText");
        var loading = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var error = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
        panel.Children.Add(selector); panel.Children.Add(loading); panel.Children.Add(body); panel.Children.Add(error);
        selector.SelectedItem = current.IsEnabled ? current : raw;
        body.Text = selector.SelectedItem == raw ? versions.Original : versions.Current;
        selector.SelectionChanged += async (_, _) =>
        {
            error.IsOpen = false;
            if (selector.SelectedItem == raw && versions.Original.Length == 0 && loadOriginal != null)
            {
                selector.IsEnabled = false; loading.Visibility = Visibility.Visible; loading.IsActive = true;
                try
                {
                    versions = versions with { Original = (await loadOriginal()).Trim() };
                    loadOriginal = null;
                    raw.IsEnabled = versions.Original.Length > 0;
                    if (!raw.IsEnabled && current.IsEnabled) selector.SelectedItem = current;
                }
                catch (Exception exception) { error.Message = exception.Message; error.IsOpen = true; if (current.IsEnabled) selector.SelectedItem = current; }
                finally { selector.IsEnabled = true; loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
            }
            body.Text = selector.SelectedItem == raw ? versions.Original : versions.Current;
        };
        return panel;
    }
    public static StackPanel SourceLinks(Models.MetadataDocument document, Models.LibraryMediaKind kind)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var id in document.Ids)
        {
            var label = id.Key.Equals("bangumi", StringComparison.OrdinalIgnoreCase) ? "Bangumi" : id.Key.Equals("imdb", StringComparison.OrdinalIgnoreCase) ? "IMDb" : id.Key.ToUpperInvariant();
            if (Services.MetadataLinks.Get(id.Key, id.Value, kind) is { } uri)
            {
                var link = new HyperlinkButton { Content = label + " #" + id.Value, NavigateUri = uri, FontSize = 12, Padding = new(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(link, "ProviderLink_" + id.Key.ToLowerInvariant()); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, label + " #" + id.Value);
                panel.Children.Add(link);
            }
            else panel.Children.Add(new TextBlock { Text = label + " #" + id.Value, FontSize = 12, Style = Style("SecondaryTextStyle") });
        }
        return panel;
    }
    public static StackPanel Label(string glyph, string label) { var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; panel.Children.Add(Icon(glyph)); panel.Children.Add(new TextBlock { Text = L.Text(label), VerticalAlignment = VerticalAlignment.Center }); return panel; }
}
