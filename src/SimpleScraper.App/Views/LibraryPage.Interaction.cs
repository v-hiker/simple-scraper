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
    private async void HandleShortcut(object sender, KeyRoutedEventArgs args)
    {
        if (_busy) return;
        static bool Down(Windows.System.VirtualKey key) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var control = Down(Windows.System.VirtualKey.Control); var shift = Down(Windows.System.VirtualKey.Shift);
        if (Down(Windows.System.VirtualKey.Menu) || Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows)) return;
        if (control && !shift && args.Key == Windows.System.VirtualKey.F) { args.Handled = true; _filter.Focus(FocusState.Keyboard); }
        else if (!control && !shift && args.Key == Windows.System.VirtualKey.F5 && !_scanning) { args.Handled = true; await Run(Scan); }
        else if (control && shift && Current != null && args.Key is Windows.System.VirtualKey.S or Windows.System.VirtualKey.R)
        {
            args.Handled = true;
            await Run(args.Key == Windows.System.VirtualKey.S ? Search : PreviewRename);
        }
    }

    private void UpdateActions()
    {
        foreach (var action in _selectionActions) action.IsEnabled = !_busy && !_scanning && !_loadingLibrary && Current != null;
        foreach (var action in _writeActions) action.IsEnabled = !_busy;
        _libraries.IsEnabled = !_busy; _spinner.IsActive = _working || _scanning || _loadingLibrary || _rendering || _initializing; _spinner.Visibility = _spinner.IsActive ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = _working; _progress.Visibility = _working || _scanning ? Visibility.Visible : Visibility.Collapsed; _cancel.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
        if (_episodeEdit != null) _episodeEdit.IsEnabled = !_busy && !_scanning && !_loadingLibrary && Selected != null && (Selected.File != null || Selected.Season == null || Selected.Season >= 0);
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy || _scanning || _loadingLibrary) return;
        var previousStatus = _status.Text;
        // A modal command owns input, but it is not background work.
        _busy = true; UpdateActions();
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = previousStatus; }
        catch (TmdbApiKeyMissingException) { await TmdbKeyPrompt.Configure(_config, XamlRoot); }
        catch (Exception e) { _status.Text = e.Message; System.Diagnostics.Debug.WriteLine(e); _services.WriteDiagnostic(e); }
        finally { _busy = false; UpdateActions(); }
    }

    private IProgress<string> WorkProgress(string operation)
    {
        var version = _workVersion;
        return new Progress<string>(name =>
        {
            if (_working && version == _workVersion) _status.Text = L.Text(operation) + " · " + name;
        });
    }

    private async Task<T> Work<T>(Func<Task<T>> action)
    {
        var previousStatus = _status.Text;
        var workingStatus = L.Text("Working…");
        _workVersion++; _working = true; _status.Text = workingStatus; UpdateActions();
        try { return await action(); }
        finally { _working = false; if (_status.Text == workingStatus) _status.Text = previousStatus; UpdateActions(); }
    }
}
