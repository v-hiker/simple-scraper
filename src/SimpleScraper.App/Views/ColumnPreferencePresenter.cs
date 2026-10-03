using SimpleScraper.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleScraper.Views;

public sealed class ColumnPreferencePresenter : UserControl
{
    public static readonly DependencyProperty PreferenceProperty = DependencyProperty.Register(nameof(Preference), typeof(object), typeof(ColumnPreferencePresenter), new PropertyMetadata(null, Changed));
    public object Preference { get => GetValue(PreferenceProperty); set => SetValue(PreferenceProperty, value); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ColumnPreferencePresenter)sender).Render(args.NewValue as LibraryColumnPreference);
    private void Render(LibraryColumnPreference? preference)
    {
        if (preference == null) { Content = null; return; }
        var row = new Grid { ColumnSpacing = 12, MinHeight = 48, Padding = new(6, 3, 6, 3) }; row.ColumnDefinitions.Add(new() { Width = new(24) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(110) });
        row.Children.Add(Ui.Icon("\uE784", 16));
        var check = new CheckBox { Content = L.Text(LibraryColumns.All.First(c => c.Id == preference.Id).Label), IsChecked = preference.Visible, IsEnabled = preference.Id != "title", VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => preference.Visible = true; check.Unchecked += (_, _) => preference.Visible = false;
        Grid.SetColumn(check, 1); row.Children.Add(check);
        var width = new NumberBox { Value = preference.Width, Minimum = 50, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, VerticalAlignment = VerticalAlignment.Center };
        width.ValueChanged += (_, _) => { if (double.IsFinite(width.Value)) preference.Width = (int)Math.Clamp(width.Value, 50, 600); };
        Grid.SetColumn(width, 2); row.Children.Add(width);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(check, "Column_" + preference.Id); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(width, "ColumnWidth_" + preference.Id);
        Content = row;
    }
}
