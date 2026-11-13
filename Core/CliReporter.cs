using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

/// <summary>Serializes concurrent FFmpeg events to stdout and an optional JSONL log.</summary>
/// <remarks>Human progress goes to stderr so stdout remains safe for PowerShell JSON pipelines.</remarks>
public sealed class CliReporter(bool json, bool verbose, Func<string, string> translate) : IDisposable
{
    public JsonObject Context { get; set; } = new();
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    private StreamWriter? log;
    private readonly object gate = new();
    public void OpenLog(string path)
    {
        try
        {
            log = new StreamWriter(path, append: true, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliException("log_unavailable", e.Message, 5); }
    }
    public void Emit(string type, JsonNode? value)
    {
        lock (gate)
        {
            if (type == "progress")
            {
                // FFmpeg out_time_us is in microseconds; speed is a realtime
                // multiplier, while fps is an independently reported frame rate.
                var p = value!.DeepClone().AsObject();
                double elapsed = Clock.Elapsed.TotalSeconds;
                double position = Math.Max(0, JsonFields.Number(p.S("out_time_us")) / 1e6), duration = JsonFields.Number(p.S("duration"));
                double speed = JsonFields.Number(p.S("speed").TrimEnd('x'));
                double fps = JsonFields.Number(p.S("fps"));
                p["position_seconds"] = position;
                p["duration_seconds"] = duration;
                p["elapsed_seconds"] = elapsed;
                p["percent"] = duration > 0 ? JsonValue.Create(Math.Min(100, 100 * position / duration)) : null;
                p["eta_seconds"] = duration > 0 && speed > 0 ? JsonValue.Create(Math.Max(0, (duration - position) / speed)) : null;
                p["fps"] = fps > 0 ? fps : JsonFields.Number(p.S("frame")) / Math.Max(elapsed, .001);
                value = p;
            }
            var record = new JsonObject { ["schema_version"] = 1, ["event"] = type };
            foreach (var p in Context)
                record[p.Key] = p.Value?.DeepClone();
            record["data"] = value?.DeepClone();
            var line = BackendJson.Write(record);
            if (json)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
            else if (type == "progress")
            {
                var p = value!;
                var text = string.Format(CultureInfo.InvariantCulture, "{0}  {1:F1}/{2:F1}s  {3:F1} fps  {4} {5:F0}s  {6} {7}",
                    p["percent"] is null ? "?%" : JsonFields.Number(p.S("percent")).ToString("F1", CultureInfo.InvariantCulture) + "%",
                    JsonFields.Number(p.S("position_seconds")), JsonFields.Number(p.S("duration_seconds")), JsonFields.Number(p.S("fps")),
                    translate("已用"), JsonFields.Number(p.S("elapsed_seconds")), translate("剩余"), p["eta_seconds"] is null ? "?" : JsonFields.Number(p.S("eta_seconds")).ToString("F0", CultureInfo.InvariantCulture) + "s");
                Console.Error.WriteLine(text);
            }
            else if (type == "log")
            {
                if (verbose)
                    Console.Error.WriteLine(value?.ToString());
            }
            else if (type is "error" or "status")
                Console.Error.WriteLine(translate(value is JsonObject o ? o.S("message") : value?.ToString() ?? ""));
            else if (type is "done" or "skipped" or "cancelled")
                Console.Out.WriteLine(translate(type switch
                {
                    "done" => "完成",
                    "skipped" => "已跳过",
                    _ => "已取消"
                }) + ": " + value);
            else if (type == "summary")
                Console.Out.WriteLine(BackendJson.Write(value));
            if (log is not null)
                try
                {
                    log.WriteLine(line);
                    log.Flush();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Dispose(); log = null; throw new CliException("log_unavailable", e.Message, 5); }
        }
    }
    public void Dispose() => log?.Dispose();
}
