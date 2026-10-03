using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleScraper.Views;

// Fixed views use SelectorBar, without the document-tab close commands of TabView.
internal sealed class SectionTabs : Grid
{
    private readonly SelectorBar _selector = new() { Margin = new(0, 0, 0, 8) };
    private readonly Grid _content = new();
    public event EventHandler? SelectionChanged;
    public SectionTabs()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Children.Add(_selector); Grid.SetRow(_content, 1); Children.Add(_content);
        _selector.SelectionChanged += (_, _) => { for (var i = 0; i < _content.Children.Count; i++) _content.Children[i].Visibility = i == SelectedIndex ? Visibility.Visible : Visibility.Collapsed; SelectionChanged?.Invoke(this, EventArgs.Empty); if (SelectedIndex >= 0) Ui.Enter(_content.Children[SelectedIndex]); };
    }
    public int SelectedIndex
    {
        get => _selector.SelectedItem == null ? -1 : _selector.Items.IndexOf(_selector.SelectedItem);
        set { if (value >= 0 && value < _selector.Items.Count && _selector.Items[value].IsEnabled) _selector.SelectedItem = _selector.Items[value]; }
    }
    public SelectorBarItem Add(string label, string glyph, UIElement content, string id, bool enabled = true)
    {
        var item = new SelectorBarItem { Text = L.Text(label), Icon = Ui.Icon(glyph, 16), IsEnabled = enabled };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, L.Text(label)); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, id);
        content.Visibility = Visibility.Collapsed; _content.Children.Add(content); _selector.Items.Add(item);
        if (_selector.SelectedItem == null && enabled) _selector.SelectedItem = item;
        return item;
    }
}
