// Developer-only adapter for Python fixture/parity tests. Production GUI and
// CLI call Core directly; the line protocol here is never shipped or used by them.
using System.Text;
using System.Text.Json.Nodes;
using VideoConverter;
using VideoConverter.Core;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() == "--check")
{
    return CodeChecks.Run();
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
                    var result = await engine.RunAsync(tools, media, options, request.Text("output"), (type, value) =>
                    {
                        if (action == "run-events" && type != "progress")
                        {
                            events.Add(new JsonObject { ["event"] = type, ["data"] = value?.DeepClone() });
                        }
                    }, seconds: request["seconds"] is null ? null : JsonFields.Number(request.Text("seconds")), overwrite: request.Flag("overwrite"));
                    answer = action == "run-events" ? new JsonObject { ["media"] = ToJson(result), ["events"] = events } : ToJson(result);
                }
                else if (action == "plan")
                {
                    var plan = engine.Plan(tools, media, options, request.Text("output"));
                    answer = new JsonObject { ["argv"] = JsonFields.Strings(plan.Arguments), ["colors"] = plan.Colors, ["options"] = plan.Options.ToJson() };
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
