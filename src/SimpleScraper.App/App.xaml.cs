using Microsoft.UI.Xaml;
using SimpleScraper.Views;

namespace SimpleScraper;

public sealed partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
    public static AppServices Services { get; private set; } = null!;

    public App()
    {
        Services = AppServices.CreateDefault();
        UnhandledException += (_, args) => Services.WriteDiagnostic(new InvalidOperationException(args.Message, args.Exception));
        try
        {
            L.Initialize(Services.Config.GetUiLanguage());
            InitializeComponent();
        }
        catch (Exception exception) { Services.WriteDiagnostic(new InvalidOperationException("Application resources could not be initialized.", exception)); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Resource dictionaries are resolved once the WinUI application has entered its launch lifecycle.
            _ = Resources["SurfaceCardStyle"];
            MainWindow = new MainWindow(Services);
            MainWindow.Activate();
        }
        catch (Exception exception) { Services.WriteDiagnostic(new InvalidOperationException("The desktop window could not be initialized.", exception)); throw; }
    }
}
