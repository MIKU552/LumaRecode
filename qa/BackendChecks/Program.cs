// Developer-only adapter for Python fixture/parity tests. Production GUI and
// CLI call Core directly; the line protocol here is never shipped or used by them.
using System.Text;
using System.Text.Json.Nodes;
using VideoConverter;
using VideoConverter.Core;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() == "--resource-tool")
{
    return await ResourceChecks.RunToolAsync(args);
}
if (args.FirstOrDefault() == "--resource-stress")
{
    await ResourceChecks.CheckLongRecordingAsync();
    return 0;
}
if (await FfmpegStub.TryRunAsync(args) is { } stubResult)
{
    return stubResult;
}
if (args.FirstOrDefault() == "--check")
{
    int result = CodeChecks.Run();
    if (result == 0)
    {
        await ResourceChecks.RunAsync();
    }
    return result;
}

var root = Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "Assets");
var engine = new Transcoder(assets);
var tools = new MediaTools(ProcessRunner.FindTool("ffmpeg"), ProcessRunner.FindTool("ffprobe"));
MediaInfo FromJson(JsonNode m) => new(m.Text("path"), m["video"]!.AsObject(), m["frames"] as JsonArray ?? [], m["tags"] as JsonObject ?? new(),
    m.Text("kind"), m["profile"]?.ToString(), m.Text("evidence"), JsonFields.Number(m.Text("duration")), m["streams"] as JsonArray ?? [], m["pixel"] as JsonObject ?? new());
JsonObject ToJson(MediaInfo m) => new()
{
    ["path"] = m.Path,
    ["video"] = m.Video.DeepClone(),
    ["frames"] = m.Frames.DeepClone(),
    ["tags"] = m.Tags.DeepClone(),
    ["kind"] = m.Kind,
    ["profile"] = m.Profile,
    ["evidence"] = m.Evidence,
    ["duration"] = m.Duration,
    ["streams"] = m.Streams.DeepClone(),
    ["pixel"] = m.Pixel.DeepClone()
};
if (args.FirstOrDefault() == "--server")
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        try
        {
            var request = JsonNode.Parse(line)!;
            var action = request.Text("action");
            JsonNode? answer;
            if (action == "inspect")
            {
                answer = ToJson(await MediaProbe.InspectAsync(tools, request.Text("path")));
            }
            else if (action == "classify")
            {
                (string, ulong)? dji = request["dji"] is JsonArray d ? (d[0]!.ToString(), ulong.Parse(d[1]!.ToString())) : null;
                var (kind, profile, evidence) = MediaProbe.Classify(request["video"]!.AsObject(), request["tags"]!.AsObject(), dji);
                answer = new JsonArray(JsonValue.Create(kind), JsonValue.Create(profile), JsonValue.Create(evidence));
            }
            else if (action == "preview-benchmark")
            {
                var media = await MediaProbe.InspectAsync(tools, request.Text("path"));
                var timeline = await FrameTimeline.LoadAsync(tools, media);
                var exporter = new FrameExporter(engine);
                var samples = new JsonArray();
                foreach (var frame in request["frames"]!.AsArray())
                {
                    var options = new ImageExportOptions(frame!.GetValue<int>(), "png", request.Text("source_kind", "auto"), request["profile"]?.ToString());
                    long allocated = GC.GetTotalAllocatedBytes();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var bytes = await exporter.PreviewAsync(tools, media, timeline, options);
                    samples.Add(new JsonObject
                    {
                        ["frame"] = options.Frame,
                        ["milliseconds"] = timer.Elapsed.TotalMilliseconds,
                        ["bytes"] = bytes.Length,
                        ["managed_allocated_bytes"] = GC.GetTotalAllocatedBytes() - allocated
                    });
                }
                answer = samples;
            }
            else if (action is "image-export" or "image-preview")
            {
                var media = await MediaProbe.InspectAsync(tools, request.Text("path"));
                var timeline = await FrameTimeline.LoadAsync(tools, media);
                var exporter = new FrameExporter(engine);
                var options = new ImageExportOptions(request.Integer("frame"), request.Text("format", "sdr"), request.Text("source_kind", "auto"), request["profile"]?.ToString(), request.Flag("motion"),
                    request["start_frame"] is null ? null : new FrameRange(request.Integer("start_frame"), request.Integer("end_frame")),
                    Purpose: request.Text("purpose", "archive"), MotionSize: request.Text("motion_size", "source"),
                    MotionVideoRange: request.Text("motion_range", "sdr"));
                if (action == "image-preview")
                {
                    File.WriteAllBytes(request.Text("output"), await exporter.PreviewAsync(tools, media, timeline, options));
                    answer = JsonValue.Create(true);
                }
                else
                {
                    var result = await exporter.ExportAsync(tools, media, options, request.Text("output"), (_, _) => { }, timeline: timeline);
                    answer = new JsonObject { ["width"] = result.Width, ["height"] = result.Height, ["bytes"] = result.Bytes, ["format"] = result.Format, ["motion_duration"] = result.MotionDuration };
                }
            }
            else if (action == "motion-photo")
            {
                await MotionPhoto.WriteAsync(File.ReadAllBytes(request.Text("path")), request.Text("video"), request.Text("output"), request.Integer("timestamp_us"));
                var info = MotionPhoto.Inspect(request.Text("output"));
                answer = new JsonObject { ["offset"] = info.VideoOffset, ["length"] = info.VideoLength, ["timestamp_us"] = info.PresentationTimestampUs, ["ultrahdr"] = info.UltraHdr };
            }
            else if (action is "ultrahdr-encode" or "ultrahdr-inspect")
            {
                var bytes = File.ReadAllBytes(request.Text("path"));
                if (action == "ultrahdr-encode")
                {
                    bytes = UltraHdrCodec.Encode(bytes, request.Integer("width"), request.Integer("height"), request.Flag("hlg"));
                    File.WriteAllBytes(request.Text("output"), bytes);
                }
                var info = UltraHdrCodec.Inspect(bytes);
                answer = new JsonObject { ["width"] = info.Width, ["height"] = info.Height, ["gain_width"] = info.GainMapWidth, ["gain_height"] = info.GainMapHeight };
            }
            else if (action == "timeline")
            {
                var info = await MediaProbe.InspectAsync(tools, request.Text("path"));
                var timeline = await FrameTimeline.LoadAsync(tools, info);
                answer = new JsonObject
                {
                    ["count"] = timeline.Count,
                    ["packet_indexed"] = timeline.PacketIndexed,
                    ["duration"] = timeline.Duration,
                    ["pts"] = new JsonArray(timeline.Frames.Select(f => (JsonNode?)JsonValue.Create(f.Pts)).ToArray())
                };
            }
            else if (action == "hdr10plus-export")
            {
                await using var output = File.Create(request.Text("output"));
                if (request["frames"] is not null)
                {
                    await Hdr10PlusMetadata.ExportAsync(new StringReader(request.Text("frames")), output);
                }
                else
                {
                    output.Close();
                    var media = await MediaProbe.InspectAsync(tools, request.Text("path"));
                    await DynamicHdr.ExtractAsync(tools, media, request.Text("output"), CancellationToken.None);
                }
                answer = JsonValue.Create(true);
            }
            else
            {
                var media = FromJson(request["media"]!);
                var options = EncodeOptions.FromJson(request["options"]);
                if (action == "public")
                {
                    answer = MediaProbe.Describe(media);
                }
                else if (action == "flac")
                {
                    var (valid, reason) = OutputPolicy.FlacEligibility(media);
                    answer = new JsonArray(JsonValue.Create(valid), JsonValue.Create(reason));
                }
                else if (action == "validate")
                {
                    answer = JsonValue.Create(engine.Validate(options, media));
                }
                else if (action == "resolve")
                {
                    answer = engine.ResolveFormat(options, media).ToJson();
                }
                else if (action is "run" or "run-events")
                {
                    var events = new JsonArray();
                    var runTools = request["ffmpeg"] is not null ? tools with
                    {
                        Ffmpeg = request.Text("ffmpeg")
                    } : tools;
                    var result = await engine.RunAsync(runTools, media, options, request.Text("output"), (type, value) =>
                    {
                        if (action == "run-events" && type != "progress")
                        {
                            events.Add(new JsonObject { ["event"] = type, ["data"] = value?.DeepClone() });
                        }
                    }, seconds: request["seconds"] is null ? null : JsonFields.Number(request.Text("seconds")), overwrite: request.Flag("overwrite"),
                        selection: request["start_frame"] is null ? null : new FrameRange(request.Integer("start_frame"), request["end_frame"] is null ? null : request.Integer("end_frame")), motionPhoto: request.Flag("motion_photo"));
                    answer = action == "run-events" ? new JsonObject { ["media"] = ToJson(result), ["events"] = events } : ToJson(result);
                }
                else if (action == "plan")
                {
                    var selection = request["start_frame"] is null ? null : new FrameRange(request.Integer("start_frame"), request["end_frame"] is null ? null : request.Integer("end_frame"));
                    var plan = await engine.PlanAsync(tools, media, options, request.Text("output"), selection, motionPhoto: request.Flag("motion_photo"));
                    answer = new JsonObject { ["argv"] = JsonFields.Strings(plan.Arguments), ["colors"] = plan.Colors, ["options"] = plan.Options.ToJson(), ["software_decode"] = plan.UsesSoftwareDecoding };
                }
                else
                {
                    throw new InvalidDataException("Unknown test operation");
                }
            }
            Console.WriteLine(BackendJson.Write(new JsonObject { ["ok"] = true, ["data"] = answer }));
        }
        catch (Exception e) { Console.WriteLine(BackendJson.Write(new JsonObject { ["ok"] = false, ["error"] = e.Message })); }
    }
    return 0;
}
var languages = new Dictionary<string, Dictionary<string, string>>();
foreach (var code in new[] { "en", "ja", "es" })
{
    languages[code] = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "App", $"Strings.{code}.json")))!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToString());
}

var catalog = new TranslationCatalog(languages);
var language = "en";
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] is "--lang" or "--language")
    {
        language = args[i + 1];
    }
}

var context = new CliContext(System.Xml.Linq.XDocument.Load(Path.Combine(root, "Core/LumaRecode.Core.csproj")).Descendants("Version").Single().Value, Path.Combine(root, "licenses"), false, tools, text => catalog.Translate(text, language), language);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await new NativeCli(engine, context).RunAsync(args, cancellation.Token);
