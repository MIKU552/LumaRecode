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
    Check(newDocument.RootElement.GetProperty(property.Name).ToString() == property.Value.ToString(), "Settings round trip: " + property.Name);
Check(newDocument.RootElement.EnumerateObject().Count() == 9, "Unexpected settings properties");
Check(JsonSerializer.Deserialize("{}", AppJsonContext.Default.UserSettings)!.Theme == "system", "Missing setting defaults changed");
Check(JsonSerializer.Deserialize("{\"a\":\"中文\"}", AppJsonContext.Default.DictionaryStringString)!["a"] == "中文", "Translation resource decoding changed");
Check(JsonSerializer.Deserialize("[3,15,2]", AppJsonContext.Default.Int32Array)!.SequenceEqual(new[] { 3, 15, 2 }), "Python version protocol changed");

var tools = new WorkerTools(@"C:\视频 & tools\ffmpeg.exe", @"D:\ffprobe.exe");
var options = new WorkerOptions("share", "hevc", "hdr", "log-hdr", "pocket3", "420", "standard", "auto", "auto", "auto", false);
var request = new WorkerRequest("convert", tools, @"C:\素材\input.mp4", @"D:\output.mp4", options);
var line = JsonSerializer.Serialize(request, WorkerJsonContext.Default.WorkerRequest);
Check(!line.Contains('\n') && !line.Contains('\r'), "Worker request must be a single JSON line");
using var document = JsonDocument.Parse(line);
var root = document.RootElement;
Check(root.GetProperty("action").GetString() == "convert" && root.GetProperty("path").GetString() == request.Path && root.GetProperty("output").GetString() == request.Output, "Worker envelope changed");
Check(root.GetProperty("tools").GetProperty("ffmpeg").GetString() == tools.Ffmpeg, "Tool path contract changed");
var encoded = root.GetProperty("options");
Check(encoded.GetProperty("color_range").GetString() == "auto", "Default color range must follow purpose");
Check(encoded.GetProperty("bit_depth").GetString() == "auto", "Default bit depth must resolve from the source");
var expected = new Dictionary<string, string> { ["purpose"]="share", ["codec"]="hevc", ["output"]="hdr", ["transform"]="log-hdr", ["profile"]="pocket3", ["sampling"]="420", ["variant"]="standard", ["audio"]="auto", ["container"]="auto", ["source_kind"]="auto" };
foreach (var (key, value) in expected) Check(encoded.GetProperty(key).GetString() == value, "Worker option changed: " + key);
Check(!encoded.GetProperty("cpu_decode").GetBoolean(), "CPU decode boolean changed");
using var probe = JsonDocument.Parse(JsonSerializer.Serialize(new WorkerRequest("probe", tools, request.Path), WorkerJsonContext.Default.WorkerRequest));
Check(probe.RootElement.GetProperty("action").GetString() == "probe" && probe.RootElement.EnumerateObject().Count() == 3, "Probe envelope includes extra options");
using var failure = JsonDocument.Parse(JsonSerializer.Serialize(
    new HostError(1, "error", new HostErrorData("runtime_unavailable", "路径含中文")), WorkerJsonContext.Default.HostError));
Check(failure.RootElement.GetProperty("schema_version").GetInt32() == 1, "Host error schema changed");
Check(failure.RootElement.GetProperty("event").GetString() == "error", "Host error event changed");
Check(failure.RootElement.GetProperty("data").GetProperty("code").GetString() == "runtime_unavailable" &&
      failure.RootElement.GetProperty("data").GetProperty("message").GetString() == "路径含中文", "Host error data changed");
Console.WriteLine($"Passed {count} settings compatibility and worker JSON contract checks without touching user files.");
