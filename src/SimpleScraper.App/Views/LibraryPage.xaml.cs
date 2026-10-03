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

internal sealed record LibrarySelection(LibraryMedia Media, int? Season = null, LocalMediaFile? File = null)
{
    public string Key => File?.Path ?? Media.Folder + (Season is int s ? "|season:" + s : "");
}
public sealed partial class LibraryPage : Page
{
    private const double WorkspaceMargin = 16;
    private const double SidebarWidth = 220;
    private const double WorkspaceColumnGap = 12;
    private const double ToolbarInset = 16;
    private const double CardBorder = 1;
    private readonly AppServices _services;
    private LibraryStateStore _store = new();
    private readonly LibraryScanner _scanner;
    private readonly LibrarySnapshotStore _snapshotStore;
    private readonly Dictionary<string, LibrarySnapshot> _snapshots = new();
    private string? _activeLibraryKey;
    private readonly LibraryRenameService _rename;
    private readonly ConfigService _config;
    private List<LibraryMedia> _media = new();
    private readonly ListView _libraries = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly DenseTable _table = new();
    private System.Collections.ObjectModel.ObservableCollection<TableRow> _visibleRows = new();
    private readonly HashSet<string> _expandingRows = new(StringComparer.OrdinalIgnoreCase);
    private ListView Items => _table.List;
    private readonly TextBox _filter = new();
    private readonly ComboBox _filterState = new() { MinWidth = 115 };
    private readonly List<Control> _selectionActions = new();
    private readonly List<Control> _writeActions = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counts = new() { Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _spinner = new() { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { Height = 3, Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = L.Text("Cancel loading"), Visibility = Visibility.Collapsed, MinHeight = 32, Height = 32, Padding = new(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly DenseTable _cast = new();
    private readonly StackPanel _art = new() { Spacing = 12 };
    private readonly SectionTabs _tabs = new();
    private HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy, _working, _scanning, _loadingLibrary, _rendering, _initializing = true, _selectionSuppressed;
    private int _scanVersion, _rowsVersion, _detailVersion, _filterVersion, _workVersion;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _rowsCancellation;
    private Control? _episodeEdit;
    private bool _initializationStarted;
    private readonly Dictionary<string, MetadataEpisode> _episodeCache = new(StringComparer.OrdinalIgnoreCase);
    private LibrarySelection? Selected => (Items.SelectedItem as TableRow)?.Tag as LibrarySelection;
    private LibraryMedia? Current => Selected?.Media;
    private MediaOutputProfile OutputProfile => MediaOutputProfile.ForLibrary(_libraries.SelectedItem as MediaLibrary, _store.Profile);

    public LibraryPage() : this(App.Services) { }

    public LibraryPage(AppServices services)
    {
        _services = services; _scanner = services.Scanner; _snapshotStore = services.Snapshots; _rename = services.Rename; _config = services.Config;
        InitializeComponent(); Build();
        Items.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection { new Microsoft.UI.Xaml.Media.Animation.AddDeleteThemeTransition() };
        // Page accelerators can attach automatic shortcut tooltips to descendant text,
        // including popup content. Handle keys without registering that tooltip owner.
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(HandleShortcut), true);
        _libraries.SelectionChanged += async (_, _) => { if (!_initializing && !_busy) await LoadLibrary(); };
        Items.SelectionChanged += async (_, _) => { if (!_selectionSuppressed) await SelectMedia(); };
        Items.DoubleTapped += async (_, _) => { if (Current != null) await Run(EditMetadata); };
        _filter.TextChanged += async (_, _) => { var version = ++_filterVersion; await Task.Delay(160); if (version == _filterVersion) await ShowRows(); };
        _filterState.SelectionChanged += async (_, _) => await ShowRows();
        _tabs.SelectionChanged += async (_, _) => { if (!_initializing) await SelectMedia(); };
        Loaded += async (_, _) =>
        {
            if (_initializationStarted) return;
            _initializationStarted = true;
            _busy = true; UpdateActions(); _status.Text = L.Text("Loading library settings…");
            try { _store = await Task.Run(_services.LoadState); _store.Columns = LibraryColumns.Normalize(_store.Columns); _expanded = _store.ExpandedRows.ToHashSet(StringComparer.OrdinalIgnoreCase); if (!LibraryColumns.All.Any(c => c.Id == _store.SortColumn)) _store.SortColumn = "title"; _libraries.ItemsSource = _store.Libraries; }
            catch (Exception e) { _status.Text = e.Message; }
            finally { _busy = false; _initializing = false; UpdateActions(); }
            if (_store.Libraries.Count > 0) _libraries.SelectedIndex = Math.Max(0, _store.Libraries.FindIndex(l => LibrarySnapshotStore.Key(l) == _store.LastLibraryKey));
        };
    }
}
