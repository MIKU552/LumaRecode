using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LumaRecode.Native;

namespace VideoConverter.Core;

/// <summary>Resolves encoding policy, builds FFmpeg arguments and publishes verified output files.</summary>
/// <remarks>Planning has no process or filesystem side effects. Execution uses a sibling
/// temporary directory and only moves a verified result to its final destination.</remarks>
public sealed partial class Transcoder
{
    public string Assets
    {
        get;
    }
    public JsonObject Config
    {
        get;
    }
    private readonly Lazy<JsonObject> motionConfig;
    public JsonObject MotionConfig => motionConfig.Value;
    // Matrix/range describe YCbCr decoding into the LUT's RGB domain, not
    // the gamut retained by the camera's Log curve. See each LUT's provenance.
    public static readonly Dictionary<string, (string Name, string File, string Matrix, string Range)> Profiles = new()
    {
        ["pocket3"] = ("DJI Pocket 3 D-Log M", "DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube", "709", "limited"),
        ["samsung"] = ("Samsung Log", "Samsung_Log_to_Rec2100_HLG.cube", "2020_ncl", "limited"),
        ["milog"] = ("小米 Mi-Log", "REDMI_K90_MiLog_to_Rec2100_HLG.cube", "2020_ncl", "full"),
        ["applelog"] = ("Apple Log", "Apple_Log_to_Rec2100_HLG.cube", "2020_ncl", "limited"),
        ["applelog2"] = ("Apple Log 2", "Apple_Log_2_to_Rec2100_HLG.cube", "2020_ncl", "limited")
    };
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
    public Transcoder(string assets)
    {
        Assets = Path.GetFullPath(assets);
        Config = JsonNode.Parse(File.ReadAllText(Path.Combine(Assets, "recommended-presets.json")))!.AsObject();
        motionConfig = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(Assets, "motion-photo-presets.json")))!.AsObject());
    }
    public EncodeOptions ResolveFormat(EncodeOptions options, MediaInfo media)
    {
        Require(options.Purpose is "share" or "archive" && options.Codec is "hevc" or "av1" or "avc", "请选择有效的用途和编码器。");
        var sourceStatus = NativeMedia.ReadSourceFormat(NativeBackend.Snapshot(media, formatOnly: true), out var source,
            out var diagnostic, options.Transform.StartsWith("log-") && Profiles.TryGetValue(options.Profile ?? "", out var p)
                ? p.Range == "full" ? ColorRange.Full : ColorRange.Limited : ColorRange.Auto);
        NativeBackend.Check(sourceStatus, diagnostic);
        var value = NativeCore.CreateOutputOptions();
        value.Purpose = options.Purpose == "share" ? Purpose.Share : Purpose.Archive;
        value.Codec = options.Codec == "hevc" ? Codec.Hevc : options.Codec == "avc" ? Codec.Avc : Codec.Av1;
        Require(options.Output is "sdr" or "hdr", "请选择 SDR 或 HDR 输出。");
        value.DynamicRange = options.Output == "hdr" ? DynamicRange.Hdr : DynamicRange.Sdr;
        value.Sampling = options.Sampling switch
        {
            "auto" => Sampling.Auto,
            "source" => Sampling.Source,
            "420" => Sampling.Yuv420,
            "422" => Sampling.Yuv422,
            "444" => Sampling.Yuv444,
            "gray" => Sampling.Gray,
            _ => throw new InvalidDataException("请选择自动、保持原采样、4:2:0、4:2:2 或 4:4:4。")
        };
        value.ColorRange = options.ColorRange switch
        {
            "auto" => ColorRange.Auto,
            "source" => source.ColorRange,
            "limited" => ColorRange.Limited,
            "full" => ColorRange.Full,
            _ => throw new InvalidDataException("请选择自动、保持原范围、Limited 或 Full。")
        };
        value.BitDepth = options.BitDepth switch
        {
            "auto" => 0,
            "8" => 8,
            "10" => 10,
            "12" => 12,
            _ => throw new InvalidDataException("请选择自动、8、10 或 12 位输出。")
        };
        NativeBackend.Check(NativeCore.ResolveFormat(source, value, out var format, out diagnostic), diagnostic);
        return options with
        {
            Sampling = OutputPolicy.SamplingName(format.Sampling),
            BitDepth = format.BitDepth.ToString(CultureInfo.InvariantCulture),
            ColorRange = format.ColorRange == ColorRange.Full ? "full" : "limited"
        };
    }

    public string Validate(EncodeOptions options, MediaInfo media)
    {
        // Pure native planning validates the complete contract without probing a
        // tool or opening a file. The supplied paths are placeholders only.
        Plan(new(Path.Combine(Assets, "ffmpeg"), Path.Combine(Assets, "ffprobe")), media, options, Path.Combine(Assets, "validation" + options.Suffix));
        return options.SourceKind == "auto" ? media.Kind : options.SourceKind;
    }

    public EncodingPlan Plan(MediaTools tools, MediaInfo media, EncodeOptions requestedOptions, string destination, double? seconds = null,
        FrameWindow? window = null, bool motionPhoto = false, string motionSize = "source", int previewSize = 0)
        => BuildPlan(tools, media, requestedOptions, destination, seconds, false, window: window, motionPhoto: motionPhoto, motionSize: motionSize, previewSize: previewSize);

    private EncodingPlan BuildPlan(MediaTools tools, MediaInfo media, EncodeOptions options, string destination, double? seconds,
        bool forceQsvTimingCompatibility, bool autoLowPower = false, FrameWindow? window = null, bool motionPhoto = false, string motionSize = "source", int previewSize = 0)
    {
        Require(seconds is null || double.IsFinite(seconds.Value) && seconds.Value > 0, "--duration 必须为有限正数。");
        ArgumentOutOfRangeException.ThrowIfNegative(previewSize);
        var request = new NativePlanRequest(NativeBackend.Snapshot(media), NativeBackend.JsonBytes(options.ToJson()),
            NativeBackend.JsonBytes(Config), Path.GetFullPath(tools.Ffmpeg), Path.GetFullPath(destination), Assets,
            motionPhoto ? NativeBackend.JsonBytes(MotionConfig) : null, seconds ?? 0,
            (forceQsvTimingCompatibility ? NativePlanFlags.QsvTiming : 0) | (autoLowPower ? NativePlanFlags.AutoLowPower : 0)
                | (motionPhoto ? NativePlanFlags.MotionPhoto : 0), (uint)previewSize,
            window is null ? null : new((ulong)window.Start, (ulong)window.End, (ulong)window.SeekFrame, window.SeekSeconds, window.StartSeconds, window.EndSeconds), motionSize);
        var result = NativePlanner.Create(request);
        NativeBackend.Check(result.Status, result.Diagnostic);
        var plan = JsonNode.Parse(result.Data)!;
        return new(plan["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray(), plan["colors"]!.DeepClone().AsObject(),
            EncodeOptions.FromJson(plan["options"]), plan.Flag("qsv_timing_compatibility"), plan["working_directory"]?.GetValue<string>())
        {
            NativeRequest = request
        };
    }

    private string ResolveAudio(EncodeOptions options) => options.Audio == "auto" ? options.Purpose == "archive" ? Config["audio"].Text("archive_default") : Config["audio"]!["share_default"].Text(options.Codec) : options.Audio;
    /// <summary>Checks the selected FFmpeg build for required encoders, formats and filters.</summary>
    /// <remarks>Hardware availability is determined when FFmpeg initializes the actual job.</remarks>
    public async Task CheckRequirementsAsync(MediaTools tools, EncodeOptions options, EncodingPlan plan, CancellationToken cancellation, ToolReportCache? reports = null)
    {
        string encoder = plan.Arguments[Array.IndexOf(plan.Arguments, "-vf") + 3];
        Task<ProcessResult> Report(string[] arguments) => reports is null
            ? ProcessRunner.CaptureAsync(tools.Ffmpeg, arguments, cancellation, 15)
            : reports.CaptureAsync(tools.Ffmpeg, arguments, cancellation);
        var result = await Report(["-hide_banner", "-encoders"]);
        bool Has(string text, string name) => Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b");
        Require(result.ExitCode == 0 && Has(result.Output, encoder), $"选中的 FFmpeg 缺少 {encoder} 编码器，请在工具路径里选择完整版本。");
        Require(options.Audio != "flac" || Has(result.Output, "flac"), "选中的 FFmpeg 缺少 FLAC 编码器。");
        if (options.Purpose == "archive")
        {
            result = await Report(["-hide_banner", "-h", "encoder=" + encoder]);
            var supported = Regex.Match(result.Output, @"Supported pixel formats:\s*([^\r\n]+)");
            Require(!supported.Success || supported.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(options.PixelFormat), $"当前 FFmpeg 的 {encoder} 不支持 {options.PixelFormat}，请更换编码器或采样格式。");
        }
        var filter = plan.Arguments[Array.IndexOf(plan.Arguments, "-vf") + 1];
        var required = new[] { "zscale", "lut3d", "libplacebo", "vpp_qsv", "sendcmd", "geq", "colorchannelmixer" }.Where(n => filter.Contains(n + "=") || filter.Contains(n + "@")).ToArray();
        if (required.Length > 0)
        {
            result = await Report(["-hide_banner", "-filters"]);
            var missing = required.Order().Where(n => !Has(result.Output, n)).ToArray();
            Require(result.ExitCode == 0 && missing.Length == 0, "选中的 FFmpeg 缺少所需滤镜 " + string.Join(", ", missing) + "，请选择完整版本。");
        }
    }
    /// <summary>Inspects staging output and applies the shared plan's media verification.</summary>
    public async Task<MediaInfo> VerifyAsync(MediaTools tools, MediaInfo source, EncodeOptions options, JsonObject expected,
        string output, CancellationToken cancellation = default, bool motionPhoto = false, string motionSize = "source", EncodingPlan? plan = null)
    {
        var result = await MediaProbe.InspectAsync(tools, output, cancellation).ConfigureAwait(false);
        plan ??= Plan(tools, source, options, output, motionPhoto: motionPhoto, motionSize: motionSize);
        var request = plan.NativeRequest! with
        {
            MediaJson = NativeBackend.Snapshot(source)
        };
        var verified = NativePlanner.VerifyOutput(request, NativeBackend.Snapshot(result));
        NativeBackend.Check(verified.Status, verified.Diagnostic);
        return result;
    }
    /// <summary>Encodes, verifies and atomically publishes a result; cancellation removes only job-owned files.</summary>
    /// <remarks>Existing outputs are replaced only after successful verification when overwrite is true.</remarks>
    public async Task<MediaInfo> RunAsync(MediaTools tools, MediaInfo media, EncodeOptions requestedOptions, string output,
        Action<string, JsonNode?> emit, CancellationToken cancellation = default, double? seconds = null, bool overwrite = false, ToolReportCache? reports = null,
        FrameRange? selection = null, bool motionPhoto = false, FrameTimeline? cachedTimeline = null, string motionSize = "source")
    {
        output = Path.GetFullPath(output);
        Require(!ProcessRunner.SameFile(output, media.Path), "不能将源文件用作输出。");
        Require(!File.Exists(output) && !Directory.Exists(output) || overwrite && File.Exists(output), "输出文件已存在，请更换文件名。");
        var parent = Path.GetDirectoryName(output)!;
        Require(Directory.Exists(parent) && Path.GetExtension(output).Equals(requestedOptions.Suffix, StringComparison.OrdinalIgnoreCase), $"请选择已有的输出目录，文件后缀应为 {requestedOptions.Suffix}。");
        Validate(requestedOptions, media);
        Require(selection is null || seconds is null, "逐帧裁剪不能同时使用 --duration。");
        FrameTimeline? timeline = null;
        FrameWindow? window = null;
        if (selection is not null)
        {
            emit("status", JsonValue.Create("正在读取逐帧时间轴…"));
            timeline = cachedTimeline?.Matches(media.Path) == true ? cachedTimeline : await FrameTimeline.LoadAsync(tools, media, cancellation);
            window = timeline.Resolve(selection);
        }
        using var transaction = NativeOutputTransaction.Create(media.Path, output, overwrite);
        string scratch = transaction.WorkspacePath;
        string temporary = transaction.StagingPath;
        DirectoryInfo? auxiliary = null;
        bool cleanupConfirmed = true;
        try
        {
            var plan = Plan(tools, media, requestedOptions, temporary, seconds, window, motionPhoto, motionSize);
            var options = plan.Options;
            var verificationSource = window is not null && DynamicHdr.Preserve(media, options)
                ? await FirstSelectedFrameAsync(tools, media, timeline!, window.Start, cancellation) : media;
            if (media.Raw is not null || DynamicHdr.Preserve(media, options) && media.Hdr10Plus)
            {
                auxiliary = Directory.CreateTempSubdirectory("LumaRecode-metadata-");
                plan = plan with
                {
                    WorkingDirectory = auxiliary.FullName
                };
            }
            await CheckRequirementsAsync(tools, options, plan, cancellation, reports);
            if (media.Raw is not null)
            {
                emit("status", JsonValue.Create("正在读取 RAW 相机参数…"));
                await RawDevelopment.PrepareAsync(tools, media, Path.Combine(auxiliary!.FullName, "raw-calibration.cmd"), seconds, cancellation, window);
            }
            if (DynamicHdr.Preserve(media, options) && media.Hdr10Plus)
            {
                emit("status", JsonValue.Create("正在提取 HDR10+ 动态元数据…"));
                await DynamicHdr.ExtractAsync(tools, media, Path.Combine(auxiliary!.FullName, "hdr10plus.json"), cancellation, selection);
            }
            if (options.Suffix == ".mp4" && ResolveAudio(options) == "copy" && media.Audio.Any())
            {
                emit("status", JsonValue.Create("正在检查 MP4 音轨兼容性…"));
                NativeProcessResult trial;
                try
                {
                    trial = await NativeProcess.RunAsync(new(tools.Ffmpeg, ["-hide_banner","-nostdin","-v","error","-xerror","-n","-i",media.Path,
                        "-map","0:a","-vn","-sn","-dn","-map_metadata","-1","-c:a","copy","-t","0.08","-f","mp4",
                        "-movflags","frag_keyframe+empty_moov","pipe:1"], OutputMode: NativeCaptureMode.Discard, TimeoutMilliseconds: 20000), cancellation);
                    NativeBackend.Check(trial.ChildExited ? NativeStatus.Ok : trial.Status,
                        trial.Diagnostic, trial.CleanupConfirmed, cancellation);
                }
                catch (TimeoutException) { throw new InvalidDataException("MP4 原音轨兼容性检测超时。请选择 AAC 音轨或 MKV 封装。"); }
                if (trial.ExitCode != 0)
                {
                    emit("log", JsonValue.Create(trial.ErrorText.Trim()));
                    throw new InvalidDataException($"当前 FFmpeg 无法将原音轨（{string.Join(", ", media.Audio.Select(s => s.Text("codec_name", "未知")).Distinct().Order())}）直接封装为 MP4。请选择 AAC 音轨，或改用 MKV 封装。");
                }
            }
            var duration = window?.Duration ?? (seconds is { } limit && media.Duration > 0 ? Math.Min(limit, media.Duration) : seconds ?? media.Duration);
            var attempt = await EncodeAsync(plan, emit, duration, cancellation);
            bool autoLowPower = false;
            while (attempt.ExitCode != 0 && options is { Purpose: "share", Hardware: "qsv" })
            {
                cancellation.ThrowIfCancellationRequested();
                bool hardwareDecode = plan.Arguments.Contains("-hwaccel");
                // AV1 HDR side-data duplication can surface only as EEXIST in
                // the filter graph. Restrict that recovery to the affected input
                // and hardware-decode path; an ordinary existing-file error is
                // never permission to overwrite or retry.
                bool av1Hdr = media.Video.Text("codec_name") == "av1" && media.SideData.Any(s =>
                    s.Text("side_data_type") is "Mastering display metadata" or "Content light level metadata");
                bool retryDecode = hardwareDecode && ((attempt.Errors & (QsvError.Decoder | QsvError.HardwareFormat)) != 0
                    || av1Hdr && attempt.Errors.HasFlag(QsvError.FilterSideData));
                bool retryPower = !autoLowPower && attempt.Errors.HasFlag(QsvError.LowPower);
                bool retryTiming = !plan.QsvTimingCompatibility && attempt.Errors.HasFlag(QsvError.Timestamp);
                if (!retryDecode && !retryPower && !retryTiming)
                {
                    break;
                }
                // Each fallback changes a single state monotonically. There
                // are at most three retries, never a loop on device failures.
                if (retryDecode)
                {
                    emit("status", JsonValue.Create("QSV 解码路径不兼容，正在改用软件解码重试…"));
                    options = options with
                    {
                        CpuDecode = true
                    };
                }
                if (retryPower)
                {
                    emit("status", JsonValue.Create("QSV 低功耗模式不受支持，正在自动选择编码模式重试…"));
                    autoLowPower = true;
                }
                if (retryTiming)
                {
                    emit("status", JsonValue.Create("检测到 QSV 时间戳冲突，正在使用兼容模式重试…"));
                }
                // Only the failed result in this job's private directory is
                // removed. The destination remains untouched until verified.
                File.Delete(temporary);
                plan = BuildPlan(tools, media, options, temporary, seconds,
                    forceQsvTimingCompatibility: plan.QsvTimingCompatibility || retryTiming, autoLowPower: autoLowPower, window: window, motionPhoto: motionPhoto, motionSize: motionSize);
                if (auxiliary is not null)
                {
                    plan = plan with
                    {
                        WorkingDirectory = auxiliary.FullName
                    };
                }

                await CheckRequirementsAsync(tools, options, plan, cancellation, reports);
                attempt = await EncodeAsync(plan, emit, duration, cancellation);
            }
            Require(attempt.ExitCode == 0, $"FFmpeg 转码失败（{attempt.ExitCode}）。请查看日志，或改用软件归档 / 软件解码。");
            emit("status", JsonValue.Create("正在核对输出色彩和采样格式…"));
            var result = await VerifyAsync(tools, verificationSource, options, plan.Colors, temporary, cancellation, motionPhoto, motionSize, plan);
            cancellation.ThrowIfCancellationRequested();
            // Recheck cancellation immediately before the only operation that
            // can replace an existing destination.
            NativeBackend.Check(transaction.Seal(true, out var sealDiagnostic), sealDiagnostic);
            NativeBackend.Check(transaction.Commit(out var publishDiagnostic), publishDiagnostic);
            return result with
            {
                Path = output
            };
        }
        catch (NativeCleanupException)
        {
            cleanupConfirmed = false;
            throw;
        }
        finally
        {
            // A failed owner retains its workspace. Only explicitly generated
            // auxiliary files are removed; an unexpected entry leaves the directory.
            if (cleanupConfirmed && auxiliary is not null)
            {
                foreach (string name in new[] { "raw-calibration.cmd", "hdr10plus.json" })
                {
                    string path = Path.Combine(auxiliary.FullName, name);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                try
                {
                    auxiliary.Delete(recursive: false);
                }
                catch (IOException) { }
            }
            transaction.Cleanup(cleanupConfirmed, out _);
        }
    }
    [Flags]
    private enum QsvError
    {
        None = 0, Timestamp = 1, Decoder = 2, LowPower = 4, FilterSideData = 8, HardwareFormat = 16
    }

    /// <summary>Runs one attempt and classifies recoverable QSV failures while streaming progress/log events.</summary>
    private async Task<(int ExitCode, QsvError Errors)> EncodeAsync(EncodingPlan plan, Action<string, JsonNode?> emit,
        double duration, CancellationToken cancellation)
    {
        emit("decoder", JsonValue.Create(plan.UsesSoftwareDecoding ? "software" : "qsv"));
        emit("log", JsonValue.Create(string.Join(' ', plan.Arguments.Select(a => '"' + a.Replace("\"", "\\\"") + '"'))));
        var errors = QsvError.None;
        var progress = new JsonObject();
        var output = new Utf8LineConsumer(line =>
        {
            var pair = line.Trim().Split('=', 2);
            if (pair.Length == 2 && pair[0] is "frame" or "out_time_us" or "speed" or "fps")
            {
                progress[pair[0]] = pair[1].Trim();
            }
            else if (pair[0] == "progress")
            {
                progress["duration"] = duration;
                emit("progress", progress.DeepClone());
            }
            else if (pair.Length == 1 && line.Trim().Length > 0)
            {
                emit("log", JsonValue.Create(line.Trim()));
            }
        });
        var diagnostic = new Utf8LineConsumer(line =>
        {
            string text = line.Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (text.Contains("Non-monotonic DTS", StringComparison.OrdinalIgnoreCase) || text.Contains("Non-monotonous DTS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase))
            {
                errors |= QsvError.Timestamp;
            }

            if (text.Contains("Error initializing the MFX video decoder", StringComparison.OrdinalIgnoreCase) || text.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Error decoding header", StringComparison.OrdinalIgnoreCase))
            {
                errors |= QsvError.Decoder;
            }

            if (text.Contains("Low power mode is unsupported", StringComparison.OrdinalIgnoreCase))
            {
                errors |= QsvError.LowPower;
            }

            if (text.Contains("[vf#", StringComparison.Ordinal) && text.Contains("(File exists)", StringComparison.Ordinal))
            {
                errors |= QsvError.FilterSideData;
            }

            if (text.Contains("Impossible to convert between the formats", StringComparison.OrdinalIgnoreCase))
            {
                errors |= QsvError.HardwareFormat;
            }

            emit("log", JsonValue.Create(text));
        });
        var result = await NativeProcess.RunStreamingAsync(new(plan.Arguments[0], plan.Arguments[1..], plan.WorkingDirectory ?? Assets,
            OutputMode: NativeCaptureMode.Discard, ErrorCapacity: 8192, TimeoutMilliseconds: 0), (stream, bytes) =>
        {
            cancellation.ThrowIfCancellationRequested();
            (stream == 1 ? output : diagnostic).Feed(bytes);
        }, cancellation).ConfigureAwait(false);
        NativeBackend.Check(result.ChildExited ? NativeStatus.Ok : result.Status,
            result.Diagnostic, result.CleanupConfirmed, cancellation);
        output.Feed([], flush: true);
        diagnostic.Feed([], flush: true);
        return (result.ExitCode, errors);
    }
}
