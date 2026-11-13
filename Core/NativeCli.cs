using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

/// <summary>Host-owned version, preferences and localization; Core does not depend on WinUI or user settings files.</summary>
public sealed record CliContext(string Version, string LicenseFolder, bool TermsAccepted, MediaTools SavedTools,
    Func<string, string> Translate, string Language = "en");

/// <summary>Headless command orchestration with deterministic plans, batch conflict checks and stable exit codes.</summary>
public sealed class NativeCli(Transcoder engine, CliContext context)
{
    private string T(string text) => context.Translate(text);
    private static string? Env(string key) => Environment.GetEnvironmentVariable("LUMARECODE_" + key) ?? Environment.GetEnvironmentVariable("FRAMEFLOW_" + key);
    private JsonArray Presets()
    {
        var rows = new JsonArray();
        foreach (var purpose in new[] { "share", "archive" })
            foreach (var item in engine.Config[purpose]!.AsObject())
                rows.Add((JsonNode)new JsonObject { ["id"] = purpose + "-" + item.Key, ["options"] = new JsonObject { ["purpose"] = purpose, ["codec"] = item.Key, ["variant"] = "standard" }, ["encoder"] = item.Value?.DeepClone() });
        rows.Add((JsonNode)new JsonObject { ["id"] = "archive-av1-fast", ["options"] = new JsonObject { ["purpose"] = "archive", ["codec"] = "av1", ["variant"] = "fast" }, ["encoder"] = engine.Config["archive_fast_av1"]?.DeepClone() });
        return rows;
    }
    private JsonObject Describe(MediaInfo media)
    {
        var result = MediaProbe.Describe(media, detailed: true);
        result["evidence"] = T(result.S("evidence"));
        result["flac_reason"] = T(result.S("flac_reason"));
        return result;
    }
    private static void Output(JsonNode? value)
    {
        Console.Out.WriteLine(BackendJson.Write(value, indented: true));
        Console.Out.Flush();
    }
    private string Tool(string name, string? requested, string saved)
    {
        var candidate = requested is not null ? File.Exists(requested) ? Path.GetFullPath(requested) : ProcessRunner.FindTool(requested)
            : ProcessRunner.FindTool(name) is { Length: > 0 } path ? path : File.Exists(saved) ? Path.GetFullPath(saved) : Env(name.ToUpperInvariant()) ?? "";
        if (!File.Exists(candidate))
            throw new CliException("tool_unavailable", T($"未找到 {name}；请使用 --{name} 指定路径。"), 3);
        return Path.GetFullPath(candidate);
    }
    // Resolve metadata-dependent CLI defaults before invoking the same core
    // validation used by the GUI. Explicit input interpretation has priority.
    private EncodeOptions Resolve(JsonObject values, MediaInfo media)
    {
        var fields = values.DeepClone().AsObject();
        var kind = fields.S("input_kind", "auto");
        if (kind == "auto")
            kind = media.Kind;
        var profile = fields.S("profile", "auto");
        if (profile == "auto")
            profile = media.Profile ?? "";
        if (kind == "unknown" && profile.Length > 0)
            kind = "log";
        if (kind == "unknown")
            throw new CliException("ambiguous_input", T("无法确认传递曲线，请指定 --input-kind。"), 4);
        var target = fields.S("range", "auto");
        if (target == "auto")
            target = kind is "hlg" or "pq" or "log" ? "hdr" : "sdr";
        var transform = fields.S("transform", "auto");
        if (transform == "auto")
            transform = kind == "log" ? target == "hdr" ? "log-hdr" : "log-sdr" : kind is "hlg" or "pq" && target == "sdr" ? "hdr-sdr" : "none";
        if (kind == "log" && profile.Length == 0)
            throw new CliException("ambiguous_log", T("读到了 Log，但无法确认曲线；请指定 --profile（见 profiles）。"), 4);
        if (values.S("profile", "auto") != "auto" && !transform.StartsWith("log-"))
            throw new CliException("invalid_arguments", T("--profile 需要 Log 转换；要覆盖错误的色彩标签，请指定 --input-kind log。"));
        fields["output"] = target;
        fields["source_kind"] = kind;
        fields["transform"] = transform;
        fields["profile"] = profile.Length > 0 ? profile : null;
        var options = EncodeOptions.FromJson(fields);
        try
        {
            engine.Validate(options, media);
            return engine.ResolveFormat(options, media);
        }
        catch (InvalidDataException e) { throw new CliException("invalid_options", T(e.Message)); }
    }
    private void Help(string? command)
    {
        Console.Out.WriteLine(T("用法：") + "LumaRecode.exe " + (command ?? "<command>") + " [options]" + (command is "convert" or "plan" or "probe" ? " INPUT ..." : ""));
        Console.Out.WriteLine();
        if (command is null)
        {
            Console.Out.WriteLine(T("同一个 EXE 提供 GUI 与命令行。无参数时打开 GUI；使用下列命令时无界面运行。"));
            Console.Out.WriteLine("\n" + T("位置参数") + ":");
            string[] descriptions = ["读取视频与全部音轨信息", "列出编码预设", "列出内置 Log/LUT 曲线", "检查工具与编码器、滤镜", "读取组件条款与许可路径", "转换文件、目录或通配符", "查看解析选项和 FFmpeg 参数，不编码"];
            for (int i = 0; i < CliArguments.Commands.Length; i++)
                Console.Out.WriteLine("  " + CliArguments.Commands[i].PadRight(12) + T(descriptions[i]));
        }
        Console.Out.WriteLine("\n" + T("选项") + ":");
        void Row(string name, string description) => Console.Out.WriteLine("  " + name.PadRight(34) + T(description));
        Row("-h, --help", "显示帮助并退出");
        Row("--version", "显示版本并退出");
        Row("--json", "机器可读 JSON；转换时为逐行 JSON");
        Row("--lang, --language {system,zh,en,ja,es}", "输出语言");
        Row("--ffmpeg PATH", "FFmpeg 路径，默认先 PATH 再使用 GUI 已保存路径");
        Row("--ffprobe PATH", "ffprobe 路径");
        Row("--accept-terms", "接受随附第三方二进制组件条款");
        Row("--no-settings", "忽略 GUI 已保存路径与许可状态，不保存设置");
        if (command == "license")
            Row("--text", "输出项目许可与组件条款全文");
        if (command is "convert" or "plan")
        {
            Row("-o, --output PATH", "单个输入的输出文件");
            Row("--output-dir PATH", "批量输出根目录，保留扫描的子目录结构");
            Row("--config PATH", "编码选项 JSON 文件");
            Console.Out.WriteLine("  --preset {" + string.Join(',', Presets().Objects().Select(p => p.S("id"))) + "}");
            foreach (var p in CliArguments.Choices)
                Console.Out.WriteLine("  --" + p.Key.Replace('_', '-') + " {" + string.Join(',', p.Value) + "}");
            Row("--duration SECONDS", "只转换指定秒数");
            Row("--overwrite", "成功编码并校验后才替换已有输出");
            Row("--log PATH", "向日志追加逐行 JSON 事件");
            Console.Out.WriteLine("  -r, --recursive\n  --cpu-decode, --no-cpu-decode\n  --skip-existing\n  --continue-on-error\n  --dry-run\n  --verbose");
        }
        Console.Out.WriteLine("\n" + T("PowerShell 7：接 | Out-Host，或在 --json 后接 | ConvertFrom-Json，以等待完成并读取 $LASTEXITCODE。"));
    }
    /// <summary>Executes one command. Query commands return JSON documents; conversion events use JSON Lines.</summary>
    public async Task<int> RunAsync(string[] words, CancellationToken cancellation = default)
    {
        CliReporter? report = null;
        try
        {
            var args = CliArguments.Parse(words);
            report = new CliReporter(args.Has("json"), args.Has("verbose"), T);
            if (args.Version)
            {
                Console.Out.WriteLine("LumaRecode " + context.Version);
                return 0;
            }
            if (args.Help || args.Command is null)
            {
                Help(args.Command);
                return 0;
            }
            if (args.Command == "presets")
            {
                Output(new JsonObject { ["schema_version"] = 1, ["presets"] = Presets() });
                return 0;
            }
            if (args.Command == "profiles")
            {
                Output(new JsonObject
                {
                    ["schema_version"] = 1,
                    ["profiles"] = new JsonArray(Transcoder.Profiles.Select(p => (JsonNode?)new JsonObject
                    {
                        ["id"] = p.Key,
                        ["name"] = T(p.Value.Name),
                        ["lut"] = p.Value.File,
                        ["input_matrix"] = p.Value.Matrix,
                        ["input_range"] = p.Value.Range,
                        ["targets"] = JsonFields.Strings(["hdr", "sdr"])
                    }).ToArray())
                });
                return 0;
            }
            if (args.Command == "license")
            {
                if (args.Has("text"))
                {
                    var original = File.ReadAllText(Path.Combine(context.LicenseFolder, "LICENSE"));
                    var terms = File.ReadAllText(Path.Combine(context.LicenseFolder, context.Language switch
                    {
                        "zh" => "THIRD-PARTY-TERMS.zh-CN.txt",
                        "ja" => "THIRD-PARTY-TERMS.ja.txt",
                        "es" => "THIRD-PARTY-TERMS.es.txt",
                        _ => "THIRD-PARTY-TERMS.txt"
                    }));
                    if (args.Has("json"))
                        Output(new JsonObject { ["schema_version"] = 1, ["project_license"] = original, ["component_terms"] = terms, ["directory"] = context.LicenseFolder });
                    else
                    {
                        Console.Out.WriteLine(original);
                        Console.Out.WriteLine(terms);
                    }
                }
                else
                    Output(new JsonObject
                    {
                        ["schema_version"] = 1,
                        ["project_license"] = "MIT",
                        ["directory"] = context.LicenseFolder,
                        ["accepted"] = args.Has("accept_terms") || context.TermsAccepted,
                        ["manifest"] = JsonNode.Parse(File.ReadAllText(Path.Combine(context.LicenseFolder, "manifest.json")))
                    });
                return 0;
            }
            if (!args.Has("accept_terms") && !context.TermsAccepted)
                throw new CliException("terms_not_accepted", T("请先阅读 LumaRecode license --text，再使用 --accept-terms 接受组件条款；命令行不会弹出 GUI。"), 6);
            var tools = new MediaTools(Tool("ffmpeg", args.Get("ffmpeg"), context.SavedTools.Ffmpeg), Tool("ffprobe", args.Get("ffprobe"), context.SavedTools.Ffprobe));
            if (args.Command == "doctor")
            {
                var values = new JsonObject { ["schema_version"] = 1, ["backend"] = "native-dotnet", ["tools"] = new JsonObject { ["ffmpeg"] = tools.Ffmpeg, ["ffprobe"] = tools.Ffprobe }, ["encoders"] = new JsonObject(), ["filters"] = new JsonObject() };
                foreach (var (name, executable) in new[] { ("ffmpeg", tools.Ffmpeg), ("ffprobe", tools.Ffprobe) })
                {
                    var result = await ProcessRunner.CaptureAsync(executable, ["-version"], cancellation, 15);
                    if (result.ExitCode != 0 || !result.Output.Contains(name + " version"))
                        throw new CliException("tool_unavailable", T($"无效的 {name} 程序：{executable}"), 3);
                    values[name + "_version"] = result.Output.Split('\n')[0].Trim();
                }
                foreach (var (flag, key, names) in new[] { ("-encoders", "encoders", new[] { "hevc_qsv", "av1_qsv", "h264_qsv", "libx265", "libsvtav1", "libx264", "aac", "libopus", "flac" }), ("-filters", "filters", new[] { "zscale", "lut3d", "libplacebo" }) })
                {
                    var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", flag], cancellation, 15);
                    if (result.ExitCode != 0)
                        throw new CliException("tool_unavailable", result.Error, 3);
                    foreach (var name in names)
                        values[key]![name] = Regex.IsMatch(result.Output, @"\b" + Regex.Escape(name) + @"\b");
                }
                values["note"] = T("硬件编码模式取决于显卡与驱动。");
                Output(values);
                return 0;
            }
            if (args.Command == "probe")
            {
                if (!File.Exists(args.Inputs[0]))
                    throw new CliException("input_unavailable", args.Inputs[0], 4);
                var value = Describe(await MediaProbe.InspectAsync(tools, args.Inputs[0], cancellation));
                value["schema_version"] = 1;
                Output(value);
                return 0;
            }
            if (args.Duration is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
                throw new CliException("invalid_arguments", T("--duration 必须为有限正数。"));
            var inputs = InputFiles.Expand(args);
            var valuesForEncoding = args.EncodingValues(Presets());
            // Plan the whole batch before creating outputs. This detects
            // collisions with every input (including hard links), not just the
            // currently encoded source, before any overwrite can occur.
            var plans = new List<(MediaInfo Media, EncodeOptions Options, string Destination)>();
            var sources = inputs.Select(p => p.Path).ToArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int failures = 0;
            foreach (var (path, relative) in inputs)
            {
                try
                {
                    var media = await MediaProbe.InspectAsync(tools, path, cancellation);
                    var options = Resolve(valuesForEncoding, media);
                    var name = $"{Path.GetFileNameWithoutExtension(path)}_{options.Output.ToUpperInvariant()}_{options.Codec}_{options.Purpose}{options.Suffix}";
                    var destination = Path.GetFullPath(args.Get("output") ?? (args.Get("output_dir") is { } dir ? Path.Combine(dir, Path.GetDirectoryName(relative) ?? "", name) : Path.Combine(Path.GetDirectoryName(path)!, name)));
                    if (seen.Contains(destination) || sources.Any(p => ProcessRunner.SameFile(destination, p)))
                        throw new CliException("output_conflict", T($"输出路径冲突：{destination}"), 5);
                    if (!Path.GetExtension(destination).Equals(options.Suffix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(destination))
                        throw new CliException("output_conflict", T($"输出必须为 {options.Suffix} 文件：{destination}"), 5);
                    if (File.Exists(destination) && !args.Has("overwrite") && !args.Has("skip_existing"))
                        throw new CliException("output_exists", T($"输出已存在：{destination}"), 5);
                    seen.Add(destination);
                    plans.Add((media, options, destination));
                }
                catch (Exception e) when (e is CliException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (e is CliException { Status: 5 } || !args.Has("continue_on_error"))
                        throw;
                    failures++;
                    report.Emit("error", new JsonObject { ["code"] = (e as CliException)?.Code ?? "input_error", ["input"] = path, ["message"] = T(e.Message) });
                }
            }
            // Opening an append-only log is itself a write. Apply the same
            // source/output identity checks before opening its stream.
            if (args.Get("log") is { } log)
            {
                if (sources.Concat(seen).Any(p => ProcessRunner.SameFile(log, p)))
                    throw new CliException("output_conflict", T("日志路径与输入或输出冲突。"), 5);
                report.OpenLog(log);
            }
            int complete = 0, skipped = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (media, options, destination) = plans[i];
                report.Context = new JsonObject { ["job"] = i + 1, ["total_jobs"] = plans.Count, ["input"] = media.Path, ["output"] = destination };
                if (File.Exists(destination) && args.Has("skip_existing"))
                {
                    skipped++;
                    report.Emit("skipped", JsonValue.Create(destination));
                    continue;
                }
                var plan = engine.Plan(tools, media, options, destination, args.Duration);
                if (args.Command == "plan" || args.Has("dry_run"))
                {
                    var value = new JsonObject
                    {
                        ["input"] = Describe(media),
                        ["output"] = destination,
                        ["options"] = options.ToJson(),
                        ["pixel_format"] = options.PixelFormat,
                        ["cwd"] = engine.Assets,
                        ["argv"] = JsonFields.Strings(plan.Arguments),
                        ["expected_colors"] = plan.Colors
                    };
                    if (args.Has("json"))
                        report.Emit("plan", value);
                    else
                        Output(value);
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    report.Clock.Restart();
                    report.Emit("start", new JsonObject { ["options"] = options.ToJson(), ["duration"] = args.Duration is { } duration ? Math.Min(duration, media.Duration) : media.Duration });
                    await engine.RunAsync(tools, media, options, destination, report.Emit, cancellation, args.Duration, args.Has("overwrite"));
                    complete++;
                    report.Emit("done", JsonValue.Create(destination));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    failures++;
                    report.Emit("error", new JsonObject { ["code"] = "conversion_failed", ["message"] = T(e.Message) });
                    if (!args.Has("continue_on_error"))
                        return 1;
                }
            }
            report.Context = new();
            if (args.Command != "plan" && !args.Has("dry_run"))
                report.Emit("summary", new JsonObject { ["completed"] = complete, ["skipped"] = skipped, ["failed"] = failures });
            return failures > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            report ??= new CliReporter(words.Contains("--json"), false, T);
            try
            {
                report.Emit("cancelled", null);
            }
            catch (IOException) { }
            return 130;
        }
        catch (Exception e) when (e is CliException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            report ??= new CliReporter(words.Contains("--json"), false, T);
            try
            {
                report.Emit("error", new JsonObject { ["code"] = (e as CliException)?.Code ?? "operation_failed", ["message"] = T(e.Message) });
            }
            catch (IOException) { return 130; }
            return (e as CliException)?.Status ?? 1;
        }
        finally { report?.Dispose(); }
    }
}
