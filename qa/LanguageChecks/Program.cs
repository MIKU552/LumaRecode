using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using VideoConverter;

int count = 0;
void Equal(string expected, string actual)
{
    if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}]");
    count++;
}
foreach (var tag in new[] { "zh", "zh-CN", "zh-TW", "zh-HK", "zh-SG", "ZH_cn" }) Equal("zh", LanguagePolicy.Resolve("system", tag));
foreach (var tag in new[] { "ja", "ja-JP", "JA_jp" }) Equal("ja", LanguagePolicy.Resolve("system", tag));
foreach (var tag in new[] { "es", "es-ES", "es-MX", "es-419", "ES_ar" }) Equal("es", LanguagePolicy.Resolve("system", tag));
foreach (var tag in new[] { "en", "en-US", "en-GB", "fr-FR", "de-DE", "", null }) Equal("en", LanguagePolicy.Resolve("system", tag));
Equal("en", LanguagePolicy.Resolve("en", "zh-CN")); Equal("zh", LanguagePolicy.Resolve("zh", "fr-FR"));
Equal("ja", LanguagePolicy.Resolve("ja", "es-MX")); Equal("es", LanguagePolicy.Resolve("es", "ja-JP"));
Equal("en", LanguagePolicy.Resolve("unknown", "fr-FR"));
Equal("ja-JP", LanguagePolicy.Culture("ja")); Equal("es-ES", LanguagePolicy.XamlLanguage("es"));
Equal(TypographyPolicy.FontStack("en"), TypographyPolicy.FontStack("es"));
foreach (var code in new[] { "zh", "en", "es", "ja" })
{
    var fonts = TypographyPolicy.FontStack(code).Split(", ");
    Equal(code switch { "zh" => "SourceHanSansCN-VF.ttf#Source Han Sans CN VF", "ja" => "SourceHanSansJP-VF.ttf#Source Han Sans JP VF", _ => "SourceSans3VF-Upright.ttf#SourceSans3VF" }, fonts[0].Split('/').Last());
    Equal("SourceCodeVF-Upright.ttf#SourceCodeVF", TypographyPolicy.MonospaceStack(code).Split(", ")[0].Split('/').Last());
    Equal(code == "ja" ? "SourceHanSansJP-VF.ttf#Source Han Sans JP VF" : "SourceHanSansCN-VF.ttf#Source Han Sans CN VF", TypographyPolicy.MonospaceStack(code).Split(", ")[1].Split('/').Last());
    Equal("Segoe UI Emoji", fonts[^2]);
    Equal("Segoe UI Symbol", fonts[^1]);
}
var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../App"));
var fontFolder = Path.Combine(app, "Assets/Fonts");
using (var fontManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "../licenses/Fonts/manifest.json"))))
{
    var records = fontManifest.RootElement.EnumerateArray().ToArray();
    Equal("4", records.Count(record => record.GetProperty("file").GetString()!.EndsWith(".ttf")).ToString());
    Equal("7", records.Length.ToString());
    foreach (var record in records)
    {
        var name = record.GetProperty("file").GetString()!;
        var path = Path.Combine(name.EndsWith(".ttf") ? fontFolder : Path.Combine(app, "../licenses/Fonts"), name);
        using var stream = File.OpenRead(path);
        Equal(record.GetProperty("sha256").GetString()!, Convert.ToHexStringLower(SHA256.HashData(stream)));
        Equal(record.GetProperty("bytes").GetInt64().ToString(), stream.Length.ToString());
    }
}
var resources = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(app, "Strings.en.json")))!;
var languages = new[] { "en", "ja", "es" }.ToDictionary(code => code, code =>
    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(app, $"Strings.{code}.json")))!);
var catalog = new TranslationCatalog(languages);
string Placeholders(string value) => string.Join(",", Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order());
foreach (var (code, values) in languages)
{
    if (!resources.Keys.ToHashSet().SetEquals(values.Keys)) throw new Exception($"Translation key mismatch: {code}");
    foreach (var (zh, translated) in values)
    {
        if (string.IsNullOrWhiteSpace(translated)) throw new Exception($"Missing {code} translation: {zh}");
        Equal(Placeholders(zh), Placeholders(translated));
        Equal(translated, catalog.Translate(zh, code));
        Equal(zh, catalog.Translate(zh, "zh"));
        if (!zh.Contains("{0}")) continue;
        var arguments = Enumerable.Range(0, Regex.Matches(zh, @"\{(\d+)\}").Max(match => int.Parse(match.Groups[1].Value)) + 1)
            .Select(i => (object)$"ARG_{i}").ToArray();
        foreach (var from in new[] { "zh", "en", "ja", "es" })
            Equal(string.Format(translated, arguments), catalog.Translate(string.Format(from == "zh" ? zh : languages[from][zh], arguments), code));
    }
}
var xml = XDocument.Load(Path.Combine(app, "MainView.xaml"));
XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
var appResources = XDocument.Load(Path.Combine(app, "App.xaml"));
Equal(TypographyPolicy.FontStack("en"), appResources.Descendants(presentation + "FontFamily").Single().Value);
foreach (var attribute in xml.Descendants().Attributes().Where(a => Regex.IsMatch(a.Value, "[\\p{IsCJKUnifiedIdeographs}]")))
    if (!resources.ContainsKey(attribute.Value)) throw new Exception("Untranslated XAML: " + attribute.Value);
XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
var localizedProperties = new[] { "Text", "Content", "Header", "PlaceholderText", "AutomationProperties.Name", "ToolTipService.ToolTip" };
var expectedViewText = xml.Descendants().SelectMany(element => element.Attributes()
    .Where(a => localizedProperties.Contains(a.Name.LocalName) && Regex.IsMatch(a.Value, "[\\p{IsCJKUnifiedIdeographs}]"))
    .Select(a => (Name: element.Attribute(xamlNamespace + "Name")?.Value ?? throw new Exception("Missing localization name"), Property: a.Name.LocalName, Original: a.Value))).ToArray();
Equal(expectedViewText.Length.ToString(), ViewText.Entries.Length.ToString());
for (var i = 0; i < expectedViewText.Length; i++)
    Equal(expectedViewText[i].ToString(), ViewText.Entries[i].ToString());
Equal("10-bit", catalog.Translate("10 位", "en"));
Equal("10 位", catalog.Translate("10-bit", "zh"));
Equal("BT.2020 HLG · 10-bit · 4:2:0", catalog.Translate("BT.2020 HLG · 10 位 · 4:2:0", "en"));
Equal("Color tags: BT.2020 · HLG · Limited range", catalog.Translate("色彩标签：BT.2020 · HLG · 有限范围", "en"));
Equal("Audio 2: AAC", catalog.Translate("音轨 2：AAC", "en"));
Equal("Converting · 123 frames", catalog.Translate("正在转换 · 123 帧", "en"));
Equal("正在转换 · 123 帧", catalog.Translate("Converting · 123 frames", "zh"));
Equal("Speed 48.5 fps", catalog.Translate("速度 48.5 fps", "en"));
Equal("x265 · medium / CRF23\n10-bit 4:2:0 · MKV", catalog.Translate("x265 · medium / CRF23\n10 位 4:2:0 · MKV", "en"));
Equal("The selected FFmpeg lacks required filters: libplacebo, zscale. Select a full build.", catalog.Translate("选中的 FFmpeg 缺少所需滤镜 libplacebo, zscale，请选择完整版本。", "en"));
Equal("10 ビット", catalog.Translate("10-bit", "ja"));
Equal("10 bits", catalog.Translate("10 ビット", "es"));
Equal("10 位", catalog.Translate("10 bits", "zh"));
Equal("BT.2020 HLG · 10 bits · 4:2:0", catalog.Translate("BT.2020 HLG · 10 ビット · 4:2:0", "es"));
Equal("Convirtiendo · 123 fotogramas", catalog.Translate("変換中 · 123 フレーム", "es"));
Equal("Audio 2: AAC", catalog.Translate("音声 2：AAC", "es"));
foreach (var (code, _) in LanguagePolicy.Choices)
    if (!File.Exists(Path.Combine(app, "../licenses", LanguagePolicy.TermsFile(code)))) throw new Exception("Missing terms: " + code);
foreach (var path in new[] { @"C:\视频\测试 位.mp4", @"C:\Users\Public\Videos\sample.mp4", "HEVC Main 10", "p010le", "", "10.0% · 00:03 / 00:30" })
    foreach (var code in new[] { "zh", "en", "ja", "es" }) Equal(path, catalog.Translate(path, code));
if (args.Length > 0)
{
    using var stream = File.OpenRead(args[0]); using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var embedded = metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).ToArray();
    foreach (var code in languages.Keys)
    {
        if (!embedded.Contains($"LumaRecode.Strings.{code}.json")) throw new Exception($"Missing embedded resource: {code}");
        count++;
    }
}
Console.WriteLine($"Passed {count} language-policy, translation, template and path-preservation checks; {resources.Count} translated entries, complete XAML coverage.");
