using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleScraper.Views;

internal static class TmdbKeyPrompt
{
    public static async Task<bool> Configure(ConfigService config, XamlRoot root)
    {
        if (config.GetApiKey().Length > 0) return true;
        var prompt = new ContentDialog
        {
            XamlRoot = root,
            Title = L.Text("TMDB API key not configured"),
            Content = new TextBlock { Text = L.Text("Enter your own TMDB API key in Settings to use TMDB."), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = L.Text("Open settings"),
            CloseButtonText = L.Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Ui.Show(prompt) != ContentDialogResult.Primary) return false;
        await Ui.Show(new SettingsDialog(config, true) { XamlRoot = root });
        return config.GetApiKey().Length > 0;
    }
}
