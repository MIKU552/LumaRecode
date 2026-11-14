using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoConverter;
using VideoConverter.Core;

if (args.FirstOrDefault() == "--binary-tool")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    Console.Error.Write(new string('x', 100_000) + "diagnostic-tail");
    if (args[2] == "wait")
    {
        await Task.Delay(Timeout.Infinite);
    }
    else
    {
        var bytes = Enumerable.Range(0, int.Parse(args[2], CultureInfo.InvariantCulture)).Select(i => (byte)i).ToArray();
        await Console.OpenStandardOutput().WriteAsync(bytes);
    }
    return 0;
}
if (args.FirstOrDefault() == "--report-tool")
{
    File.AppendAllText(args[1], "run\n");
    await Task.Delay(int.Parse(args[2], CultureInfo.InvariantCulture));
    Console.WriteLine("capability report");
    return int.Parse(args[3], CultureInfo.InvariantCulture);
}
if (args.Contains("--check"))
{
    await RuntimeChecks.RunAsync();
    await PreviewChecks.RunAsync();
    return 0;
}
if (args.Contains("--tools"))
{
    await CapabilityBenchmarks.RunAsync();
    return 0;
}

// Isolated microbenchmarks. Allocation figures cover this process only;
// they do not represent the native GUI working set or FFmpeg encoding speed.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var languages = new[] { "en", "ja", "es" }.ToDictionary(code => code, code =>
    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "App", $"Strings.{code}.json")))!);
var catalog = new TranslationCatalog(languages);
string[] texts = ["选择视频", "速度 328.8 fps", "正在转换 · 123 帧", "BT.2020 HLG · 10 位 · 4:2:0",
    @"C:\素材\source 123.mp4", "x265 · medium / CRF23", "00:00 / 00:02", "Audio 2: AAC"];
var events = Enumerable.Range(0, 32).Select(i => new JsonObject
{
    ["frame"] = (i * 100).ToString(CultureInfo.InvariantCulture),
    ["out_time_us"] = (i * 1_000_000).ToString(CultureInfo.InvariantCulture),
    ["fps"] = "328.8",
    ["speed"] = "12.4x",
    ["duration"] = 60
}).ToArray();
long checksum = 0;
var results = new List<object>();
void Measure(string name, int iterations, Action action)
{
    action();
    var samples = new List<(double Ms, long Bytes)>();
    for (int trial = 0; trial < 5; trial++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        samples.Add((Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated));
    }
    results.Add(new
    {
        name,
        iterations,
        median_ms = samples.Select(s => s.Ms).Order().ElementAt(2),
        median_allocated_bytes = samples.Select(s => s.Bytes).Order().ElementAt(2)
    });
}
Measure("translation_catalog", 10, () => { var value = new TranslationCatalog(languages); checksum += value.Translate("选择视频", "en").Length; });
Measure("translation_lookup", 1000, () => { foreach (var text in texts) { checksum += catalog.Translate(text, "en").Length; } });
// v4.0.4 GUI bridge: clone, serialize, parse and clone again before reading.
Measure("gui_json_bridge", 10000, () =>
{
    var value = events[(int)(checksum % events.Length)].DeepClone();
    using var document = JsonDocument.Parse(BackendJson.Write(value));
    checksum += document.RootElement.Clone().GetProperty("frame").GetString()!.Length;
});
// A detached JsonNode snapshot can instead be consumed directly in-process.
Measure("gui_direct_snapshot", 10000, () =>
{
    checksum += events[(int)(checksum % events.Length)]["frame"]!.ToString().Length;
});
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = Environment.Version.ToString(),
    results,
    checksum
},
    new JsonSerializerOptions { WriteIndented = true }));
return 0;
