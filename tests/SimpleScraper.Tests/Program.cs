using System.Text.Json;
using System.Text.RegularExpressions;
using SimpleScraper.Localization;
using SimpleScraper.Models;
using SimpleScraper.Services;

var repository = args.FirstOrDefault(a => !a.StartsWith("--")) is { } argument
    ? Path.GetFullPath(argument) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var temporary = Path.Combine(Path.GetTempPath(), "SimpleScraper-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var oldDataHome = Environment.GetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME");
Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", Path.Combine(temporary, "settings"));
int checks = 0;
void Check(bool success, string description)
{
    if (!success) throw new InvalidOperationException(description);
    checks++;
    if (args.Contains("--verbose")) Console.WriteLine("PASS " + description);
}
try
{
    var catalogs = Path.Combine(repository, "src", "SimpleScraper.Core", "Localization");
    var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(catalogs, "en-US.json")))!;
    var chinese = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(catalogs, "zh-CN.json")))!;
    Check(english.Keys.Order().SequenceEqual(chinese.Keys.Order()), "Translation catalog keys differ.");
    foreach (var (key, value) in english)
    {
        Check(key == Localizer.Key(value), "Translation key: " + key);
        var slots = Regex.Matches(value, @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order();
        var translated = Regex.Matches(chinese[key], @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order();
        var plural = value.EndsWith("{1}") && new[] { "file{1}", "sub{1}", "warning{1}", "duplicate{1}", "season{1}" }.Any(value.Contains);
        Check((plural ? slots.Where(s => s != "{1}") : slots).SequenceEqual(translated), "Translation placeholders: " + key);
    }
    Localizer.Initialize("zh-CN");
    Check(Localizer.Text("Settings") == "设置", "Embedded Chinese resources load.");
    DomainContractsWorkflowTests.Run(Check);
    await AdapterContractTests.Run(temporary, Check);
    await InfrastructureWorkflowTests.Run(temporary, Check);
    await LibraryWorkflowTests.Run(temporary, Check);
    RecognitionWorkflowTests.Run(temporary, Check);
    ExtrasWorkflowTests.Run(temporary, Check);
    await ExperienceWorkflowTests.Run(temporary, Check);
    await TranslationProviderWorkflowTests.Run(temporary, Check);
    await TmdbCredentialsWorkflowTests.Run(repository, Check);
    await MetadataEditingWorkflowTests.Run(temporary, Check);
    ScanPerformanceWorkflowTests.Run(temporary, Check);
    WorkFolderRenameTests.Run(temporary, Check);
    Console.WriteLine($"PASS: {checks} assertions. Mocked HTTP; isolated temporary media and configuration.");
}
catch (Exception error) { Console.Error.WriteLine($"FAIL after {checks} assertions: {error}"); Environment.ExitCode = 1; }
finally
{
    Environment.SetEnvironmentVariable("SIMPLE_SCRAPER_DATA_HOME", oldDataHome);
    Directory.Delete(temporary, true);
}
