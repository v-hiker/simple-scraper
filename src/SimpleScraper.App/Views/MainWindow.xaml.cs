using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace SimpleScraper.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly UISettings _systemAppearance = new();
    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = "简单刮削器 · SimpleScraper";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleRegion);
        AppWindow.Resize(new SizeInt32(1440, 900));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SimpleScraper.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ApplyTheme(services.Config.GetUiTheme());
        Workspace.Content = new LibraryPage(services);
        WindowRoot.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        _systemAppearance.ColorValuesChanged += SystemAppearanceChanged;
        Closed += (_, _) => _systemAppearance.ColorValuesChanged -= SystemAppearanceChanged;
        UpdateCaptionTheme();
    }

    private void SystemAppearanceChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_services.Config.GetUiTheme().Equals("system", StringComparison.OrdinalIgnoreCase)) ApplyTheme("system");
    });

    public void ApplyTheme(string theme)
    {
        WindowRoot.RequestedTheme = Ui.Theme(theme);
        if (App.MainWindow != null) Ui.ApplyTheme(theme);
        UpdateCaptionTheme();
    }

    private void UpdateCaptionTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var caption = AppWindow.TitleBar;
        caption.ButtonBackgroundColor = Colors.Transparent;
        caption.ButtonInactiveBackgroundColor = Colors.Transparent;
        caption.ButtonForegroundColor = WindowRoot.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        caption.ButtonInactiveForegroundColor = WindowRoot.ActualTheme == ElementTheme.Dark ? Colors.Gray : Colors.DarkGray;
    }
}
