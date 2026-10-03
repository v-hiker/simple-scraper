using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleScraper.Models;
using SimpleScraper.Services;

namespace SimpleScraper.Views;

/// <summary>Fresh settings view; edits are staged until the user presses Save.</summary>
public sealed class SettingsDialog : ContentDialog
{
    private readonly ConfigService _config;
    private readonly PasswordBox _key;
    private readonly ComboBox _theme, _language, _scrapeLanguage, _output, _translator;
    private readonly CheckBox _tmdb, _bangumi;
    private readonly TextBox _email, _libreEndpoint;
    private readonly PasswordBox _googleKey, _libreKey;
    private readonly InfoBar _feedback = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    public bool Saved { get; private set; }
    public bool LanguageChanged { get; private set; }

    public SettingsDialog(bool focusTmdb = false) : this(App.Services.Config, focusTmdb) { }

    public SettingsDialog(ConfigService config, bool focusTmdb = false)
    {
        _config = config;
        Title = S("设置", "Settings");
        PrimaryButtonText = S("保存", "Save"); CloseButtonText = S("取消", "Cancel"); DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 820d;
        var tabs = new SectionTabs { Width = 700, Height = 580 };
        var appearance = Panel();
        _theme = Choice(S("主题", "Theme"), new[] { ("system", S("跟随系统", "System")), ("light", S("浅色", "Light")), ("dark", S("深色", "Dark")) }, config.GetUiTheme().ToLowerInvariant(), "SettingsTheme");
        _language = Choice(S("界面语言", "Interface language"), new[] { ("zh-CN", "简体中文"), ("en-US", "English"), ("system", S("跟随系统", "System")) }, config.GetUiLanguage(), "SettingsUiLanguage");
        appearance.Children.Add(_theme); appearance.Children.Add(_language); appearance.Children.Add(Note(S("主题保存后立即生效，界面语言在下次启动时生效。", "The theme changes immediately after saving. Interface language changes on the next launch.")));
        tabs.Add(S("外观", "Appearance"), "\uE790", Scroll(appearance), "SettingsAppearance");

        var metadata = Panel();
        _key = Password(S("TMDB API 密钥", "TMDB API key"), config.GetApiKey(), "SettingsTmdbKey");
        metadata.Children.Add(_key);
        var getKey = new HyperlinkButton { Content = S("获取自己的 TMDB API 密钥", "Get a personal TMDB API key"), NavigateUri = new("https://www.themoviedb.org/settings/api"), Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
        Id(getKey, "SettingsGetTmdbKey"); metadata.Children.Add(getKey);
        metadata.Children.Add(Note(S("可留空。需要使用 TMDB 时再填写你自己的密钥。Bangumi 和本地资料管理无需此密钥。", "This field may be empty. Enter your own key when using TMDB. Bangumi and local library management do not need it.")));
        _scrapeLanguage = Choice(S("刮削语言", "Metadata language"), new[] { ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("en-US", "English"), ("ja-JP", "日本語"), ("ko-KR", "한국어"), ("fr-FR", "Français"), ("de-DE", "Deutsch"), ("es-ES", "Español"), ("it-IT", "Italiano"), ("pt-BR", "Português"), ("ru-RU", "Русский"), ("ar-SA", "العربية"), ("nl-NL", "Nederlands"), ("pl-PL", "Polski"), ("tr-TR", "Türkçe"), ("sv-SE", "Svenska") }, config.GetScrapeLanguage(), "SettingsScrapeLanguage");
        metadata.Children.Add(_scrapeLanguage);
        metadata.Children.Add(Note(S("影响获取的标题、简介和翻译目标，不会改变界面语言。", "Controls metadata titles, synopsis and translation targets. The interface language is configured separately.")));
        metadata.Children.Add(Heading(S("默认搜索来源", "Default search sources")));
        var sources = config.GetDefaultMetadataSources();
        _tmdb = new CheckBox { Content = "TMDB", IsChecked = sources.Contains("TMDB") }; Id(_tmdb, "SettingsSourceTMDB");
        _bangumi = new CheckBox { Content = "Bangumi", IsChecked = sources.Contains("Bangumi") }; Id(_bangumi, "SettingsSourceBangumi");
        metadata.Children.Add(_tmdb); metadata.Children.Add(_bangumi);
        _output = Choice(S("新媒体库默认输出格式", "Default output for new libraries"), new[] { ("Jellyfin", "Jellyfin"), ("Emby", "Emby") }, config.GetDefaultOutputProfile(), "SettingsOutputProfile");
        metadata.Children.Add(_output); metadata.Children.Add(Note(S("每个媒体库可以单独选择 Jellyfin 或 Emby。写入 NFO、图片和重命名均使用该媒体库的设置。", "Each library can choose Jellyfin or Emby. NFO, artwork and rename operations follow the library setting.")));
        tabs.Add(S("元数据与输出", "Metadata & output"), "\uE8A5", Scroll(metadata), "SettingsMetadata");

        var translation = Panel(); var settings = config.GetTranslationSettings();
        _translator = Choice(S("简介翻译服务", "Synopsis translation service"), TranslationProviders.Options.Select(o => (o.Id, L.Text(o.Name))), settings.Provider, "SettingsTranslationSource");
        _email = Input(S("MyMemory 邮箱（可选）", "MyMemory email (optional)"), settings.MyMemoryEmail, "SettingsTranslationEmail");
        _googleKey = Password(S("Google Cloud API 密钥", "Google Cloud API key"), settings.GoogleApiKey, "SettingsGoogleKey");
        _libreEndpoint = Input(S("LibreTranslate 服务地址", "LibreTranslate endpoint"), settings.LibreEndpoint, "SettingsLibreEndpoint");
        _libreKey = Password(S("LibreTranslate 密钥（按服务要求）", "LibreTranslate key (if required)"), settings.LibreApiKey, "SettingsLibreKey");
        translation.Children.Add(_translator); translation.Children.Add(_email); translation.Children.Add(_googleKey); translation.Children.Add(_libreEndpoint); translation.Children.Add(_libreKey);
        translation.Children.Add(Note(S("翻译仅在你点击翻译时发送简介文本，结果标注为机器翻译。原文会保留。", "Synopsis text is sent only when you request a translation. Results are labelled as machine translation; the original text is kept.")));
        void ShowProviderFields()
        {
            var provider = Value(_translator);
            _email.Visibility = provider == "MyMemory" ? Visibility.Visible : Visibility.Collapsed;
            _googleKey.Visibility = provider == "GoogleCloud" ? Visibility.Visible : Visibility.Collapsed;
            _libreEndpoint.Visibility = _libreKey.Visibility = provider == "LibreTranslate" ? Visibility.Visible : Visibility.Collapsed;
        }
        _translator.SelectionChanged += (_, _) => ShowProviderFields(); ShowProviderFields();
        tabs.Add(S("翻译", "Translation"), "\uE8D2", Scroll(translation), "SettingsTranslation");

        var about = Panel(); about.Children.Add(Heading("简单刮削器 · SimpleScraper")); about.Children.Add(Note(S("轻量的 Windows 电影与剧集资料整理工具。独立实现，采用 MIT 许可证。", "A lightweight Windows tool for movie and TV metadata. Independently implemented and distributed under the MIT License.")));
        about.Children.Add(Note(S("流程：添加媒体库 → 搜索并确认 → 刮削 → 按需重命名。", "Workflow: add a library → search and confirm → scrape → optionally rename.")));
        about.Children.Add(Note(S("快捷键：Ctrl+F 筛选；F5 刷新；Ctrl+Shift+S 搜索；Ctrl+Shift+R 重命名。", "Shortcuts: Ctrl+F filter; F5 refresh; Ctrl+Shift+S search; Ctrl+Shift+R rename.")));
        about.Children.Add(Note("MIT License · " + typeof(App).Assembly.GetName().Version?.ToString(3)));
        if (App.Services.RepositoryUri is { } repository)
        {
            var project = new HyperlinkButton { Content = S("项目主页与问题反馈", "Project home & issues"), NavigateUri = repository, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
            Id(project, "SettingsProjectHome"); about.Children.Add(project);
        }
        tabs.Add(S("关于", "About"), "\uE946", Scroll(about), "SettingsAbout");
        var container = new Grid { RowSpacing = 12 }; container.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); container.RowDefinitions.Add(new() { Height = GridLength.Auto }); container.Children.Add(tabs); Grid.SetRow(_feedback, 1); container.Children.Add(_feedback); Content = container;
        PrimaryButtonClick += Save;
        Opened += (_, _) => { if (focusTmdb) { tabs.SelectedIndex = 1; _key.Focus(FocusState.Programmatic); } };
        Loaded += (_, _) => { tabs.Width = Math.Max(450, Math.Min(700, XamlRoot.Size.Width - 120)); tabs.Height = Math.Max(300, Math.Min(580, XamlRoot.Size.Height - 240)); };
        Id(this, "SettingsDialog");
    }

    private void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var sources = new List<string>(); if (_tmdb.IsChecked == true) sources.Add("TMDB"); if (_bangumi.IsChecked == true) sources.Add("Bangumi");
            if (sources.Count == 0) throw new ArgumentException(S("至少选择一个默认搜索来源。", "Choose at least one default search source."));
            var endpoint = _libreEndpoint.Text.Trim();
            if (Value(_translator) == "LibreTranslate" && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException(S("LibreTranslate 地址须以 http:// 或 https:// 开头。", "The LibreTranslate endpoint must use http:// or https://."));
            LanguageChanged = Value(_language) != _config.GetUiLanguage();
            _config.SavePreferences(new AppConfig
            {
                TmdbApiKey = _key.Password.Trim(),
                UiTheme = Value(_theme),
                UiLanguage = Value(_language),
                ScrapeLanguage = Value(_scrapeLanguage),
                DefaultMetadataSources = sources,
                DefaultOutputProfile = Value(_output),
                Translation = new TranslationSettings { Provider = Value(_translator), MyMemoryEmail = _email.Text.Trim(), GoogleApiKey = _googleKey.Password.Trim(), LibreEndpoint = endpoint, LibreApiKey = _libreKey.Password.Trim() },
            });
            App.MainWindow.ApplyTheme(Value(_theme)); Saved = true;
        }
        catch (Exception exception) { args.Cancel = true; _feedback.Message = exception.Message; _feedback.IsOpen = true; }
    }
    private static string S(string chinese, string english) => L.Language == "zh-CN" ? chinese : english;
    private static StackPanel Panel() => new() { Spacing = 14, Padding = new(0, 6, 8, 8) };
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = Ui.Style("SecondaryTextStyle"), FontSize = 12 };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private static void Id(DependencyObject element, string value) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, value);
    private static string Value(ComboBox choice) => (choice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private static ComboBox Choice(string label, IEnumerable<(string Id, string Label)> options, string selected, string id)
    {
        var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in options) { var item = new ComboBoxItem { Content = option.Label, Tag = option.Id }; box.Items.Add(item); if (option.Id == selected) box.SelectedItem = item; }
        if (box.SelectedItem == null) box.SelectedIndex = 0; Id(box, id); return box;
    }
    private static TextBox Input(string label, string value, string id) { var box = new TextBox { Header = label, Text = value }; Id(box, id); return box; }
    private static PasswordBox Password(string label, string value, string id) { var box = new PasswordBox { Header = label, Password = value, PasswordRevealMode = PasswordRevealMode.Peek }; Id(box, id); return box; }
}
