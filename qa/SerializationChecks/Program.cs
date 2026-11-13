// Verify persisted settings and public startup-error JSON using the actual
// source-generated contexts. All migration writes stay in an isolated temp root.
using System.Text.Json;
using VideoConverter;

int count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
const string oldSettings = """
{"Theme":"dark","Language":"zh","Backdrop":"mica","SystemAccent":false,"CustomColor":"#107C10","Ffmpeg":"C:\\视频 & tools\\ffmpeg.exe","Ffprobe":"D:\\ffprobe.exe","Python":"","AcceptedThirdPartyTerms":"windows-runtime-terms-2026-11-12-mit"}
""";
var settings = JsonSerializer.Deserialize(oldSettings, AppJsonContext.Default.UserSettings)!;
Check(settings.Language == "zh" && settings.Ffmpeg == @"C:\视频 & tools\ffmpeg.exe", "Existing settings or Unicode paths changed");
using var oldDocument = JsonDocument.Parse(oldSettings);
using var newDocument = JsonDocument.Parse(JsonSerializer.Serialize(settings, AppJsonContext.Default.UserSettings));
foreach (var property in oldDocument.RootElement.EnumerateObject())
    if (property.Name != "Python")
        Check(newDocument.RootElement.GetProperty(property.Name).ToString() == property.Value.ToString(), "Settings round trip: " + property.Name);
Check(newDocument.RootElement.EnumerateObject().Count() == 8, "Unexpected settings properties");
Check(JsonSerializer.Deserialize("{}", AppJsonContext.Default.UserSettings)!.Theme == "system", "Missing setting defaults changed");
Check(JsonSerializer.Deserialize("{\"a\":\"中文\"}", AppJsonContext.Default.DictionaryStringString)!["a"] == "中文", "Translation resource decoding changed");

// Startup errors are consumed by PowerShell JSON pipelines before WinUI loads.
using var failure = JsonDocument.Parse(JsonSerializer.Serialize(
    new HostError(1, "error", new HostErrorData("runtime_unavailable", "路径含中文")), CliErrorJsonContext.Default.HostError));
Check(failure.RootElement.GetProperty("schema_version").GetInt32() == 1, "Host error schema changed");
Check(failure.RootElement.GetProperty("event").GetString() == "error", "Host error event changed");
Check(failure.RootElement.GetProperty("data").GetProperty("code").GetString() == "runtime_unavailable" &&
      failure.RootElement.GetProperty("data").GetProperty("message").GetString() == "路径含中文", "Host error data changed");
var sandbox = Directory.CreateTempSubdirectory("LumaRecode-settings-");
try
{
    var legacyFolder = Path.Combine(sandbox.FullName, "FrameFlow");
    Directory.CreateDirectory(legacyFolder);
    var legacyFile = Path.Combine(legacyFolder, "settings.json");
    File.WriteAllText(legacyFile, oldSettings);
    var migrated = UserSettings.LoadFrom(sandbox.FullName);
    var preferred = Path.Combine(sandbox.FullName, "LumaRecode", "settings.json");
    Check(migrated.Language == "zh" && migrated.Ffmpeg == settings.Ffmpeg && migrated.AcceptedThirdPartyTerms == settings.AcceptedThirdPartyTerms, "FrameFlow preferences were not inherited");
    Check(File.Exists(preferred), "Migrated settings were not saved under LumaRecode");
    Check(File.ReadAllText(legacyFile) == oldSettings, "Migration changed the original FrameFlow settings");
    migrated.Language = "es";
    migrated.SaveTo(sandbox.FullName);
    Check(UserSettings.LoadFrom(sandbox.FullName).Language == "es", "New settings must take precedence over FrameFlow");
    Check(File.ReadAllText(legacyFile) == oldSettings, "Saving LumaRecode changed FrameFlow settings");

    var olderBase = Path.Combine(sandbox.FullName, "older");
    Directory.CreateDirectory(Path.Combine(olderBase, "VideoConverterWinUI"));
    File.WriteAllText(Path.Combine(olderBase, "VideoConverterWinUI/settings.json"), oldSettings);
    Check(UserSettings.LoadFrom(olderBase).Theme == "dark", "Original VideoConverterWinUI settings were not inherited");
    Check(File.Exists(Path.Combine(olderBase, "LumaRecode/settings.json")), "Original settings were not migrated");
    var emptyBase = Path.Combine(sandbox.FullName, "empty");
    Check(UserSettings.LoadFrom(emptyBase).Theme == "system" && !Directory.Exists(emptyBase), "Empty settings load should use defaults without writing");
    File.WriteAllText(preferred, "{broken");
    Check(UserSettings.LoadFrom(sandbox.FullName).Theme == "system", "Malformed current settings should use defaults");
    Check(File.ReadAllText(legacyFile) == oldSettings, "Malformed current settings changed the legacy file");
    Check(!Directory.EnumerateFiles(sandbox.FullName, "*.tmp", SearchOption.AllDirectories).Any(), "Atomic settings save left temporary files");
}
finally
{
    var relative = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(sandbox.FullName));
    if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || !sandbox.Name.StartsWith("LumaRecode-settings-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected test cleanup directory");
    Directory.Delete(sandbox.FullName, true);
}
Console.WriteLine($"Passed {count} settings compatibility, migration and command-line JSON contract checks without touching user files.");
