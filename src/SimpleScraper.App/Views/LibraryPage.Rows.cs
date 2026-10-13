using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using System.Xml.Linq;
using System.Diagnostics;
using SimpleScraper.Presentation;

namespace SimpleScraper.Views;

public sealed partial class LibraryPage
{
    private async Task ShowRows(bool preserveScroll = false, string? operation = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var scroll = preserveScroll ? _table.ScrollOffset : 0;
        _rowsCancellation?.Cancel(); var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); _rowsCancellation = cancellation;
        var version = ++_rowsVersion; var selected = Selected?.Key; var columns = _store.Columns.Where(c => c.Visible).ToList(); var query = _filter.Text; var state = _filterState.SelectedIndex; var sort = _store.SortColumn; var descending = _store.SortDescending; var media = _media.ToList(); var expanded = _expanded.ToHashSet(StringComparer.OrdinalIgnoreCase); _rendering = true; UpdateActions();
        try
        {
            var timing = Stopwatch.StartNew();
            var rows = await Task.Run(() => BuildRows(media, columns, expanded, query, state, sort, descending, cancellation.Token), cancellation.Token);
            if (version != _rowsVersion || cancellation.IsCancellationRequested) return;
            if (operation != null) _services.WritePerformance(operation, "rows-project", timing.Elapsed.TotalMilliseconds, media.Count, rows.Count, refresh);
            timing.Restart();
            _table.SetColumns(columns.Select(c => (L.Text(LibraryColumns.All.First(d => d.Id == c.Id).Label) + (sort == c.Id ? descending ? " ↓" : " ↑" : ""), (double)c.Width)), async index => { try { _store.SortDescending = _store.SortColumn == columns[index].Id && !_store.SortDescending; _store.SortColumn = columns[index].Id; await Save(); await ShowRows(true); } catch (Exception e) { _status.Text = e.Message; } }, async (index, width, completed) => { columns[index].Width = (int)width; if (completed) try { await Save(); } catch (Exception e) { _status.Text = e.Message; } }, async (from, boundary) => { var layout = LibraryColumns.Normalize(_store.Columns); if (_busy || !LibraryColumns.MoveVisible(layout, from, boundary)) return; _store.Columns = layout; try { await Save(); await ShowRows(true); } catch (Exception e) { _status.Text = e.Message; } }, columns.Select(c => c.Id).ToList());
            _selectionSuppressed = true;
            try { _visibleRows = new(rows); Items.ItemsSource = _visibleRows; Items.SelectedItem = rows.FirstOrDefault(r => (r.Tag as LibrarySelection)?.Key == selected) ?? rows.FirstOrDefault(); }
            finally { _selectionSuppressed = false; }
            _table.RestoreScroll(scroll); _counts.Text = L.Format($"Media: {media.Count}; visible rows: {rows.Count}");
            if (operation != null) _services.WritePerformance(operation, "rows-bind", timing.Elapsed.TotalMilliseconds, media.Count, rows.Count, refresh);
            timing.Restart(); await SelectMedia();
            if (operation != null && version == _rowsVersion) _services.WritePerformance(operation, "selection-details", timing.Elapsed.TotalMilliseconds, media.Count, rows.Count, refresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (version == _rowsVersion) { _selectionSuppressed = false; _status.Text = e.Message; } }
        finally { cancellation.Dispose(); if (version == _rowsVersion) { _rowsCancellation = null; _rendering = false; UpdateActions(); } }
    }

    private List<TableRow> BuildRows(List<LibraryMedia> media, List<LibraryColumnPreference> columns, HashSet<string> expanded, string query, int state, string sort, bool descending, CancellationToken cancellationToken = default)
    {
        var projected = LibraryRowProjection.Build(media, columns, expanded, query, state, sort, descending, cancellationToken);
        var rows = new List<TableRow>(projected.Count);
        foreach (var row in projected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = new LibrarySelection(row.Media, row.Season, row.File);
            rows.Add(new() { Tag = selection, Depth = row.Depth, Header = row.File == null, Expanded = row.Expanded, Toggle = row.Expanded != null ? async () => await ToggleRow(selection) : null, ContextMenu = () => ContextMenu(selection), Cells = columns.Select((column, index) => Cell(selection, column, row.Values[index])).ToList() });
        }
        return rows;
    }

    private async Task ToggleRow(LibrarySelection selection)
    {
        // Keep the ItemsSource, existing containers, header, selection and detail
        // pane intact. Only the descendants of this node change.
        if (_rendering || _loadingLibrary || !_expandingRows.Add(selection.Key)) return;
        try
        {
            var parent = _visibleRows.FirstOrDefault(r => (r.Tag as LibrarySelection)?.Key == selection.Key);
            if (parent?.Expanded == null) return;
            var scroll = _table.ScrollOffset; var version = _rowsVersion;
            var expand = parent.Expanded != true; List<TableRow> children = new();
            if (expand)
            {
                var expanded = _expanded.ToHashSet(StringComparer.OrdinalIgnoreCase);
                expanded.Add(selection.Key); expanded.Add(new LibrarySelection(selection.Media).Key);
                var columns = _store.Columns.Where(c => c.Visible).ToList();
                var branch = await Task.Run(() => BuildRows(new() { selection.Media }, columns, expanded, "", 0, "title", false));
                children = branch.SkipWhile(r => (r.Tag as LibrarySelection)?.Key != selection.Key).Skip(1).TakeWhile(r => r.Depth > parent.Depth).ToList();
            }
            if (version != _rowsVersion || _rendering || !_visibleRows.Contains(parent)) return;
            var index = _visibleRows.IndexOf(parent); var selected = Items.SelectedItem as TableRow; var removedSelection = false;
            _selectionSuppressed = true;
            try
            {
                if (expand)
                {
                    _expanded.Add(selection.Key);
                    foreach (var child in children) _visibleRows.Insert(++index, child);
                }
                else
                {
                    _expanded.Remove(selection.Key);
                    while (index + 1 < _visibleRows.Count && _visibleRows[index + 1].Depth > parent.Depth)
                    {
                        removedSelection |= ReferenceEquals(_visibleRows[index + 1], selected);
                        _visibleRows.RemoveAt(index + 1);
                    }
                }
                parent.Expanded = expand;
                if (removedSelection) Items.SelectedItem = parent;
                else if (selected != null) Items.SelectedItem = selected;
            }
            finally { _selectionSuppressed = false; }
            _table.RestoreScroll(scroll); _counts.Text = L.Format($"Media: {_media.Count}; visible rows: {_visibleRows.Count}");
            if (removedSelection) await SelectMedia();
            _store.ExpandedRows = _expanded.ToList(); await Save();
        }
        catch (Exception e) { _status.Text = e.Message; }
        finally { _expandingRows.Remove(selection.Key); }
    }

    private static TableCell Cell(LibrarySelection selection, LibraryColumnPreference column, string value)
    {
        var tone = column.Id == "match" ? value == L.Text("Matched") ? CellTone.Success : value == L.Text("Needs manual matching") || value == L.Text("Partially matched") ? CellTone.Warning : CellTone.Muted : column.Id is "nfo" or "poster" ? value == "✓" ? CellTone.Success : value == L.Text("Needs attention") ? CellTone.Warning : CellTone.Muted : CellTone.None;
        var glyph = column.Id == "title" ? selection.File != null ? "\uE714" : selection.Season != null ? "\uE8B7" : selection.Media.Kind == LibraryMediaKind.Series ? "\uE7F4" : "\uE8B2" : column.Id == "match" ? tone == CellTone.Success ? "\uE73E" : tone == CellTone.Warning ? "\uE7BA" : "\uE8BD" : "";
        return new(value, column.Width) { Tone = tone, Glyph = glyph, IsTreeColumn = column.Id == "title" };
    }

    private MenuFlyout ContextMenu(LibrarySelection selection)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Entry(string text, string glyph, Func<Task> action) { var item = new MenuFlyoutItem { Text = L.Text(text), Icon = Ui.Icon(glyph) }; item.Click += async (_, _) => { Items.SelectedItem = (Items.ItemsSource as IEnumerable<TableRow>)?.FirstOrDefault(r => (r.Tag as LibrarySelection)?.Key == selection.Key); await Task.Delay(100); await Run(action); }; return item; }
        if (selection.File != null || selection.Season == null || selection.Season >= 0) menu.Items.Add(Entry("Edit metadata", "\uE70F", EditMetadata));
        if (selection.File is { } file) menu.Items.Add(Entry("Set season / episode", "\uE70F", () => MapFile(file)));
        if (selection.File == null && selection.Season == null) menu.Items.Add(Entry("Set media type", "\uE8F1", () => SetMediaType(selection.Media)));
        menu.Items.Add(Entry("Search and match", "\uE721", Search)); menu.Items.Add(Entry(selection.File != null ? "Scrape this episode" : "Scrape selected", "\uE896", Scrape)); menu.Items.Add(Entry("Update metadata", "\uE74E", UpdateMetadata)); menu.Items.Add(Entry("Preview rename", "\uE8A5", PreviewRename)); return menu;
    }
}
