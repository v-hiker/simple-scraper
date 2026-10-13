using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleScraper.Models;
using SimpleScraper.Services;

namespace SimpleScraper.Views;

public sealed class LibraryNavigationRow : UserControl
{
    public static readonly DependencyProperty LibraryProperty = DependencyProperty.Register(nameof(Library), typeof(object), typeof(LibraryNavigationRow), new PropertyMetadata(null, Changed));
    public object Library { get => GetValue(LibraryProperty); set => SetValue(LibraryProperty, value); }
    private readonly TextBlock _name = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _kind = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Style = Ui.Style("SecondaryTextStyle") };
    private readonly TextBlock _path = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Style = Ui.Style("SecondaryTextStyle") };
    private readonly Button _more = new() { Content = Ui.Icon("\uE712", 16), Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new(0), VerticalAlignment = VerticalAlignment.Center, Style = Ui.Style("SubtleButtonStyle") };
    private LibraryPage? _owner;
    private ListViewItem? _container;
    private MenuFlyout? _menu;

    public LibraryNavigationRow()
    {
        var row = new Grid { ColumnSpacing = 8, MinWidth = 0 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, MinWidth = 0 };
        labels.Children.Add(_name); labels.Children.Add(_kind); labels.Children.Add(_path); row.Children.Add(labels);
        Grid.SetColumn(_more, 1); row.Children.Add(_more); Content = row;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var row = (LibraryNavigationRow)sender;
        if (args.NewValue is not MediaLibrary library) { row.Detach(); return; }
        row._name.Text = library.Name; row._kind.Text = library.KindLabel; row._path.Text = library.Path;
        ToolTipService.SetToolTip(row._name, library.Name); ToolTipService.SetToolTip(row._path, library.Path);
        var moreLabel = L.Format($"More actions for {library.Name}");
        AutomationProperties.SetName(row._more, moreLabel);
        AutomationProperties.SetAutomationId(row._more, "LibraryMore_" + LibrarySnapshotStore.Key(library));
        ToolTipService.SetToolTip(row._more, moreLabel);
        row.Attach();
    }

    private void Attach()
    {
        if (Library is not MediaLibrary) return;
        LibraryPage? owner = null; ListViewItem? container = null;
        for (var node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem item) container = item;
            if (node is LibraryPage page) { owner = page; break; }
        }
        if (owner is null) return;
        if (_owner != owner) Detach();
        _owner = owner; _container = container; owner.RegisterLibraryRow(this);
    }

    private void Detach()
    {
        _menu?.Hide();
        if (_container != null && _container.ContextFlyout == _menu) _container.ContextFlyout = null;
        _more.Flyout = null; ContextFlyout = null; _menu = null; _container = null;
        _owner?.UnregisterLibraryRow(this); _owner = null;
    }

    internal void SetMenu(MenuFlyout menu)
    {
        _menu?.Hide(); _menu = menu; _more.Flyout = menu; ContextFlyout = menu;
        if (_container != null) _container.ContextFlyout = menu;
    }

    internal void SetActionsEnabled(bool enabled) => _more.IsEnabled = enabled;
}
