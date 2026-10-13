using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;

namespace SimpleScraper.Views;

public enum CellTone { None, Success, Warning, Critical, Info, Muted }
public sealed record TableCell(string Text, double Width, bool Added = false, bool Removed = false)
{
    public Func<FrameworkElement>? Editor { get; init; }
    public CellTone Tone { get; init; }
    public string Glyph { get; init; } = "";
    public bool IsTreeColumn { get; init; }
    public bool Wrap { get; init; }
}
public sealed class TableRow : System.ComponentModel.INotifyPropertyChanged
{
    public object? Tag { get; init; }
    public List<TableCell> Cells { get; init; } = new();
    public int Depth { get; init; }
    public bool Header { get; init; }
    public bool? Checked { get; init; }
    public bool CheckEnabled { get; init; } = true;
    private bool? _expanded;
    public bool? Expanded { get => _expanded; set { if (_expanded == value) return; _expanded = value; PropertyChanged?.Invoke(this, new(nameof(Expanded))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public Action<bool>? CheckChanged { get; init; }
    public Action? Toggle { get; init; }
    public Func<MenuFlyout>? ContextMenu { get; init; }
    public override string ToString() => string.Join(" · ", Cells.Select(c => c.Text).Where(t => t.Length > 0));
}

// XAML controls are created for the viewport and a short scroll cache; lists hold row data.
public sealed class DenseRowPresenter : UserControl
{
    private Grid? _grid;
    private Button? _expander;
    public DenseRowPresenter() { RightTapped += (_, e) => Ui.SelectRow(e.OriginalSource as DependencyObject); Loaded += (_, _) => SyncWidths(); }
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(object), typeof(DenseRowPresenter), new PropertyMetadata(null, Changed));
    public object Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var presenter = (DenseRowPresenter)sender;
        if (args.OldValue is TableRow old) old.PropertyChanged -= presenter.RowChanged;
        if (args.NewValue is TableRow row) row.PropertyChanged += presenter.RowChanged;
        presenter.Render(args.NewValue as TableRow);
    }
    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(TableRow.Expanded) || sender is not TableRow row || _expander == null) return;
        ((FontIcon)_expander.Content).Glyph = row.Expanded == true ? "\uE70D" : "\uE76C";
        var title = row.Cells.FirstOrDefault(c => c.IsTreeColumn)?.Text ?? row.Cells.FirstOrDefault()?.Text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_expander, L.Text(row.Expanded == true ? "Collapse" : "Expand") + " " + title);
    }
    private void Render(TableRow? row)
    {
        _expander = null;
        if (row == null) { Content = null; return; }
        // The container owns hover, selection and focus. Row backgrounds must not cover those states.
        var grid = _grid = new Grid { MinHeight = 36 };
        var treeIndex = row.Cells.FindIndex(c => c.IsTreeColumn); if (treeIndex < 0) treeIndex = 0;
        foreach (var cell in row.Cells) grid.ColumnDefinitions.Add(new() { Width = new(cell.Width) });
        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            var tree = i == treeIndex;
            if (cell.Editor != null)
            {
                var editor = cell.Editor(); editor.Margin = new(6, 3, 6, 3); Grid.SetColumn(editor, i); grid.Children.Add(editor); continue;
            }
            var area = new Grid { Padding = new(8, 6, 8, 6) };
            area.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); area.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var lead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(tree ? row.Depth * 17 : 0, 0, 0, 0) };
            if (tree && row.Checked is bool selected)
            {
                var check = new CheckBox { IsChecked = selected, IsEnabled = row.CheckEnabled, MinWidth = 28, MinHeight = 22, Padding = new(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, row.Cells.Count > 1 ? row.Cells[1].Text : cell.Text);
                check.Checked += (_, _) => row.CheckChanged?.Invoke(true); check.Unchecked += (_, _) => row.CheckChanged?.Invoke(false); lead.Children.Add(check);
            }
            if (tree && row.Expanded is bool expanded)
            {
                var button = new Button { Content = new FontIcon { Glyph = expanded ? "\uE70D" : "\uE76C", FontSize = 10 }, Width = 24, MinWidth = 24, Height = 24, Padding = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
                _expander = button;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.Text(expanded ? "Collapse" : "Expand") + " " + cell.Text); button.Click += (_, _) => row.Toggle?.Invoke(); lead.Children.Add(button);
            }
            var tone = cell.Added ? CellTone.Success : cell.Removed ? CellTone.Critical : cell.Tone;
            var style = Ui.ToneStyle(tone);
            if (cell.Glyph.Length > 0) lead.Children.Add(new TextBlock { Text = cell.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Style = style });
            area.Children.Add(lead);
            var text = new TextBlock { Text = cell.Text, FontSize = row.Header && tree ? 14 : 13, TextTrimming = cell.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = cell.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.Header && tree ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Style = style };
            ToolTipService.SetToolTip(text, cell.Text); Grid.SetColumn(text, 1); area.Children.Add(text); Grid.SetColumn(area, i); grid.Children.Add(area);
        }
        Content = new Border { Child = grid, Style = Ui.Style("RowDividerStyle") };
        if (row.ContextMenu != null) ContextFlyout = row.ContextMenu(); else ContextFlyout = null;
        SyncWidths();
    }
    internal void ResizeColumn(int index, double width) { if (_grid != null && index < _grid.ColumnDefinitions.Count) _grid.ColumnDefinitions[index].Width = new(width); }
    private void SyncWidths()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is DenseTable table) { var widths = table.ColumnWidths; if (Row is TableRow row && row.Cells.Count == widths.Count) for (var i = 0; i < widths.Count; i++) ResizeColumn(i, widths[i]); break; }
    }
}

internal sealed class ColumnResizeThumb : UserControl
{
    public Thumb Handle { get; } = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
    public ColumnResizeThumb(InputSystemCursorShape shape = InputSystemCursorShape.SizeWestEast) { ProtectedCursor = InputSystemCursor.Create(shape); IsTabStop = false; Handle.IsTabStop = false; Handle.Template = (ControlTemplate)Application.Current.Resources["ColumnDragThumbTemplate"]; Content = Handle; }
}

internal sealed class DenseTable : Grid
{
    public ListView List { get; } = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new(0) };
    private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _body = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _empty = new() { IsHitTestVisible = false, Margin = new(16, 50, 16, 16) };
    private readonly List<Grid> _headers = new();
    private readonly List<Border> _headerCues = new();
    private int? _draggedHeader;
    public DenseTable()
    {
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto }); _body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); _body.Children.Add(new Border { Child = _header, Style = Ui.Style("TableHeaderStyle") }); Grid.SetRow(List, 1); _body.Children.Add(List);
        List.ItemTemplate = (DataTemplate)Application.Current.Resources["DenseRowTemplate"];
        List.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["DenseRowsPanel"];
        List.ItemContainerStyle = Ui.Style("FluentTableItemStyle");
        Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Children.Add(_empty); SetEmptyState("No items", "Nothing to display yet.");
        List.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, (_, _) => _empty.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed);
        List.Items.VectorChanged += (_, _) => _empty.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetEmptyState(string title, string message) => _empty.Child = Ui.Empty(title, message);
    public double ScrollOffset => Ui.FindChild<ScrollViewer>(List)?.VerticalOffset ?? 0;
    internal IReadOnlyList<double> ColumnWidths => _headers.Select(h => h.Width).ToList();
    public void RestoreScroll(double offset) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.FindChild<ScrollViewer>(List)?.ChangeView(null, offset, null, true));
    public void SetColumns(IEnumerable<(string Label, double Width)> columns, Action<int>? sort = null, Action<int, double, bool>? resize = null, Action<int, int>? reorder = null, IReadOnlyList<string>? ids = null)
    {
        _header.Children.Clear(); _headers.Clear(); _headerCues.Clear(); var index = 0; double total = 0;
        foreach (var (label, width) in columns)
        {
            var i = index++; total += width;
            var frame = new Grid { Width = width, MinHeight = 38, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) }; _headers.Add(frame); _header.Children.Add(frame);
            if (sort != null)
            {
                var button = new Button { Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new(8, 6, resize == null ? 8 : 14, 6), Style = Ui.Style("TableHeaderButtonStyle") };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "ColumnHeader_" + (ids?[i] ?? i.ToString()));
                button.Click += (_, _) => { if (_draggedHeader == null) sort(i); }; frame.Children.Add(button);
                if (reorder != null)
                {
                    var drag = new ColumnResizeThumb(InputSystemCursorShape.Arrow) { Margin = new(0, 0, resize == null ? 0 : 10, 0) }; frame.Children.Add(drag);
                    var cue = new Border { Width = 3, Background = Ui.Brush("AccentFillColorDefaultBrush"), Visibility = Visibility.Collapsed, IsHitTestVisible = false }; frame.Children.Add(cue); _headerCues.Add(cue);
                    double shift = 0, shiftY = 0, start = 0, startY = 0; var dragging = false; var boundary = i;
                    drag.Handle.DragStarted += (_, e) => { shift = shiftY = 0; start = e.HorizontalOffset; startY = e.VerticalOffset; dragging = false; boundary = i; };
                    drag.Handle.DragDelta += (_, e) =>
                    {
                        shift += e.HorizontalChange; shiftY += e.VerticalChange; if (!dragging && Math.Abs(shift) < 6) return;
                        dragging = true; _draggedHeader = i; button.Opacity = .55;
                        var x = _headers.Take(i).Sum(h => h.Width) + start + shift; double offset = 0; boundary = _headers.Count;
                        for (var target = 0; target < _headers.Count; target++) { if (x < offset + _headers[target].Width / 2) { boundary = target; break; } offset += _headers[target].Width; }
                        foreach (var indicator in _headerCues) indicator.Visibility = Visibility.Collapsed;
                        var indicatorIndex = Math.Min(boundary, _headerCues.Count - 1); _headerCues[indicatorIndex].HorizontalAlignment = boundary == _headerCues.Count ? HorizontalAlignment.Right : HorizontalAlignment.Left; _headerCues[indicatorIndex].Visibility = Visibility.Visible;
                    };
                    drag.Handle.DragCompleted += (_, e) => { _draggedHeader = null; button.Opacity = 1; foreach (var indicator in _headerCues) indicator.Visibility = Visibility.Collapsed; if (e.Canceled || startY + shiftY < -20 || startY + shiftY > frame.ActualHeight + 20) return; if (dragging) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => reorder(i, boundary)); else sort(i); };
                }
            }
            else frame.Children.Add(new Border { MinHeight = 38, Padding = new(8, 6, 8, 6), Child = new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Style = Ui.Style("SecondaryTextStyle") } });
            if (resize != null)
            {
                var grip = new ColumnResizeThumb { Width = 10, HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, L.Text("Resize column") + " " + label); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(grip, "ColumnResize_" + (ids?[i] ?? i.ToString()));
                frame.Children.Add(grip); var currentWidth = width;
                grip.Handle.DragStarted += (_, _) => currentWidth = frame.Width;
                grip.Handle.DragDelta += (_, e) => { currentWidth = Math.Clamp(currentWidth + e.HorizontalChange, 50, 600); ResizeColumn(i, Math.Round(currentWidth)); resize(i, Math.Round(currentWidth), false); };
                var initialWidth = width;
                grip.Handle.DragStarted += (_, _) => initialWidth = frame.Width;
                grip.Handle.DragCompleted += (_, e) => { ResizeColumn(i, e.Canceled ? initialWidth : frame.Width, true); resize(i, frame.Width, true); };
                frame.Children.Add(new Border { Width = 1, Margin = new(0, 9, 0, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = Ui.Brush("DividerStrokeColorDefaultBrush"), IsHitTestVisible = false });
            }
        }
        _body.Width = total + 16;
    }
    private void ResizeColumn(int index, double width, bool commit = false)
    {
        _headers[index].Width = width; _body.Width = _headers.Sum(h => h.Width) + 16;
        if (commit) foreach (var row in List.Items.OfType<TableRow>()) if (index < row.Cells.Count) row.Cells[index] = row.Cells[index] with { Width = width };
        if (Ui.FindChild<ItemsStackPanel>(List) is { } panel) foreach (var container in panel.Children.OfType<ListViewItem>()) Ui.FindChild<DenseRowPresenter>(container)?.ResizeColumn(index, width);
    }
}
