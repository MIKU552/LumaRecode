using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using VideoConverter;

int count = 0;
void Equal(string expected, string actual)
{
    if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}]");
    count++;
}
foreach (var tag in new[] { "zh", "zh-CN", "zh-TW", "zh-HK", "zh-SG", "ZH_cn" }) Equal("zh", LanguagePolicy.Resolve("system", tag));
foreach (var tag in new[] { "en", "en-US", "en-GB", "fr-FR", "ja-JP", "de-DE", "", null }) Equal("en", LanguagePolicy.Resolve("system", tag));
Equal("en", LanguagePolicy.Resolve("en", "zh-CN")); Equal("zh", LanguagePolicy.Resolve("zh", "fr-FR"));
var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../App"));
var resources = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(app, "Strings.en.json")))!;
var catalog = new TranslationCatalog(resources);
foreach (var (zh, en) in resources)
{
    if (string.IsNullOrWhiteSpace(en)) throw new Exception("Missing translation: " + zh);
    Equal(en, catalog.Translate(zh, "en"));
    Equal(zh, catalog.Translate(zh, "zh"));
}
var xml = XDocument.Load(Path.Combine(app, "View.xaml.txt"));
foreach (var attribute in xml.Descendants().Attributes().Where(a => Regex.IsMatch(a.Value, "[\\p{IsCJKUnifiedIdeographs}]")))
    if (!resources.ContainsKey(attribute.Value)) throw new Exception("Untranslated XAML: " + attribute.Value);
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
foreach (var path in new[] { @"C:\视频\测试 位.mp4", @"C:\Users\Public\Videos\sample.mp4", "HEVC Main 10", "p010le", "", "10.0% · 00:03 / 00:30" })
{ Equal(path, catalog.Translate(path, "en")); Equal(path, catalog.Translate(path, "zh")); }
if (args.Length > 0)
{
    using var stream = File.OpenRead(args[0]); using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var embedded = metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).ToArray();
    if (!embedded.Contains("FrameFlow.Strings.en.json")) throw new Exception("English resource missing from built application assembly");
    count++;
}
Console.WriteLine($"Passed {count} language-policy, translation, template and path-preservation checks; {resources.Count} translated entries, complete XAML coverage.");
