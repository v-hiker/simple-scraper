using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleScraper.Models;
using SimpleScraper.Services;

namespace SimpleScraper.Views;

public sealed partial class LibraryPage
{
    private readonly HashSet<LibraryNavigationRow> _libraryRows = new();
    private bool CanManageLibraries => _stateReady && !_initializing && !_busy && !_scanning && !_loadingLibrary;

    internal void RegisterLibraryRow(LibraryNavigationRow row)
    {
        _libraryRows.Add(row);
        if (row.Library is MediaLibrary library) row.SetMenu(CreateLibraryMenu(library));
        row.SetActionsEnabled(CanManageLibraries);
    }

    internal void UnregisterLibraryRow(LibraryNavigationRow row) => _libraryRows.Remove(row);

    private MenuFlyout CreateLibraryMenu(MediaLibrary library)
    {
        var menu = new MenuFlyout();
        var key = LibrarySnapshotStore.Key(library);
        var settings = new MenuFlyoutItem { Text = L.Text("Library settings"), Icon = Ui.Icon("\uE713") };
        Id(settings, "LibraryMenuSettings_" + key); AutomationProperties.SetName(settings, L.Format($"Library settings: {library.Name}"));
        var removeIcon = Ui.Icon("\uE74D"); removeIcon.Foreground = Ui.Brush("StatusCriticalBrush");
        var remove = new MenuFlyoutItem { Text = L.Text("Remove library"), Icon = removeIcon, Foreground = Ui.Brush("StatusCriticalBrush") };
        Id(remove, "LibraryMenuRemove_" + key); AutomationProperties.SetName(remove, L.Format($"Remove library: {library.Name}"));
        menu.Items.Add(settings); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(remove);
        menu.Opening += (_, _) => settings.IsEnabled = remove.IsEnabled = CanManageLibraries && _store.Libraries.Contains(library);
        settings.Click += async (_, _) => { if (CanManageLibraries && _store.Libraries.Contains(library)) await Run(() => ConfigureLibrary(library)); };
        remove.Click += async (_, _) => { if (CanManageLibraries && _store.Libraries.Contains(library)) await Run(() => ConfirmRemoveLibrary(library)); };
        return menu;
    }
}
