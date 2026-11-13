using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

/// <summary>Resolves encoding policy, builds FFmpeg arguments and publishes verified output files.</summary>
/// <remarks>Planning has no process or filesystem side effects. Execution uses a sibling
/// temporary directory and only moves a verified result to its final destination.</remarks>
public sealed class Transcoder
{
    public string Assets
    {
        get;
    }
    public JsonObject Config
    {
        get;
    }
    // Matrix/range describe YCbCr decoding into the LUT's RGB domain, not
    // the gamut retained by the camera's Log curve. See each LUT's provenance.
    public static readonly Dictionary<string, (string Name, string File, string Matrix, string Range)> Profiles = new()
    {
        ["milog"] = ("小米 Mi-Log", "REDMI_K90_MiLog_to_Rec2100_HLG.cube", "2020_ncl", "full"),
        ["pocket3"] = ("Pocket 3 D-Log M", "DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube", "709", "limited"),
        ["samsung"] = ("Samsung Log", "Samsung_Log_to_Rec2100_HLG.cube", "2020_ncl", "limited")
    };
    private static JsonObject Rec709() => new() { ["color_primaries"] = "bt709", ["color_transfer"] = "bt709", ["color_space"] = "bt709", ["color_range"] = "tv" };
    private static JsonObject Hlg() => new() { ["color_primaries"] = "bt2020", ["color_transfer"] = "arib-std-b67", ["color_space"] = "bt2020nc", ["color_range"] = "tv" };
    private static readonly (string Key, string Flag)[] colorFlags = [("color_primaries", "-color_primaries"), ("color_transfer", "-color_trc"), ("color_space", "-colorspace"), ("color_range", "-color_range")];
    private static bool Hdr(string kind) => kind is "hlg" or "pq";
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
    private static string N(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    public Transcoder(string assets)
    {
        Assets = Path.GetFullPath(assets);
        Config = JsonNode.Parse(File.ReadAllText(Path.Combine(Assets, "recommended-presets.json")))!.AsObject();
    }
    /// <summary>Resolves source-dependent defaults and enforces codec, sampling and depth limits.</summary>
    /// <remarks>Call before using PixelFormat or Depth; their values must be concrete.</remarks>
    public EncodeOptions ResolveFormat(EncodeOptions o, MediaInfo m)
    {
        Require(o.Purpose is "share" or "archive" && o.Codec is "hevc" or "av1" or "avc", "请选择有效的用途和编码器。");
        Require(o.Output != "hdr" || o.Codec != "avc", "HDR 输出请选择 HEVC 或 AV1。");
        Require(o.Sampling is "auto" or "source" or "420" or "422" or "444" or "gray", "请选择自动、保持原采样、4:2:0、4:2:2 或 4:4:4。");
        var sampling = o.Sampling == "auto" ? OutputPolicy.DefaultSampling(m, o.Purpose, o.Codec) : o.Sampling;
        if (sampling == "source")
        {
            Require(!m.Pixel["flags"].B("alpha"), "素材含透明通道。请选择具体采样格式，输出为不带透明通道的视频。");
            sampling = OutputPolicy.SourceSampling(m);
        }
        Require(sampling is "420" or "422" or "444" or "gray", "当前编码器无法保持原采样，请手动选择 4:2:0、4:2:2 或 4:4:4。");
        Require(OutputPolicy.SamplingChoices(m).Contains(sampling), "输出采样须在原片采样上限内选择。");
        Require(sampling == "420" || o.Purpose == "archive" && o.Codec != "av1", "保持此采样需要软件 HEVC 或 H.264；也可手动选择 4:2:0。");
        Require(o.ColorRange is "auto" or "source" or "limited" or "full", "请选择自动、保持原范围、Limited 或 Full。");
        var range = o.ColorRange == "auto" ? o.Purpose == "archive" ? "source" : "limited" : o.ColorRange;
        if (range == "source")
            range = OutputPolicy.SourceRange(m, o.Transform.StartsWith("log-") && Profiles.TryGetValue(o.Profile ?? "", out var p) ? p.Range : null);
        Require(o.BitDepth is "auto" or "8" or "10" or "12", "请选择自动、8、10 或 12 位输出。");
        var depths = OutputPolicy.DepthChoices(m, o.Purpose, o.Codec, o.Output);
        Require(depths.Length > 0, "HDR 输出需要至少 10 位的源素材，请选择 SDR 或高位深原片。");
        var depth = o.BitDepth == "auto" ? OutputPolicy.DefaultDepth(m, o.Purpose, o.Codec, o.Output)!.Value : int.Parse(o.BitDepth, CultureInfo.InvariantCulture);
        Require(depths.Contains(depth), "输出位深须在原片和编码器的位深上限内选择；HDR 至少需要 10 位。");
        return o with
        {
            Sampling = sampling,
            ColorRange = range,
            BitDepth = depth.ToString(CultureInfo.InvariantCulture)
        };
    }
    /// <summary>Checks transform/source compatibility and returns the effective input kind.</summary>
    /// <remarks>A manual SourceKind overrides detection; a Log profile is only valid for Log transforms.</remarks>
    public string Validate(EncodeOptions o, MediaInfo m)
    {
        o = ResolveFormat(o, m);
        Require(o.Container is "auto" or "mp4" or "mkv", "请选择自动、MP4 或 MKV 封装。");
        Require(o.Output is "sdr" or "hdr", "请选择 SDR 或 HDR 输出。");
        Require(o.Audio is "auto" or "copy" or "aac" or "opus" or "flac", "请选择有效的音轨格式。");
        if (o.Audio == "flac")
        {
            Require(o.Container != "mp4", "FLAC 音轨请选择 MKV 封装，以保留声道布局。");
            OutputPolicy.FlacTracks(m);
        }
        Require(o.Variant == "standard" || o.Variant == "fast" && o.Purpose == "archive" && o.Codec == "av1", "快速备用质量档仅用于软件归档 AV1。");
        var kind = o.SourceKind == "auto" ? m.Kind : o.SourceKind;
        Require(kind is "sdr" or "hlg" or "pq" or "log" or "unknown", "请选择有效的输入类型。");
        if (o.Transform is "log-hdr" or "log-sdr")
        {
            Require(Profiles.ContainsKey(o.Profile ?? ""), "请选择具体的 Log 曲线。");
            Require(o.Transform == (o.Output == "hdr" ? "log-hdr" : "log-sdr"), "Log 转换目标需要与 SDR/HDR 输出一致。");
            Require(kind == "log", "Log 转换需要将输入解释指定为 Log。");
            Require(OutputPolicy.SourceDepth(m) >= 10, "这些 Log 转换需要对应相机的高位深原片。");
            return "log";
        }
        if (o.Transform == "hdr-sdr")
            Require(o.Output == "sdr" && Hdr(kind), "HDR → SDR 需要 HLG/PQ 输入及 SDR 输出。");
        else if (o.Transform == "none")
        {
            Require(kind is not ("log" or "unknown"), "请确认输入类型。Log 原片需要选择曲线后转换。");
            Require(o.Output != "hdr" || kind != "sdr", "SDR 输入请选择 SDR 输出。");
            Require(o.Output != "sdr" || !Hdr(kind), "HDR 输入输出 SDR 时，请选择 HDR → SDR 转换。");
        }
        else
            throw new InvalidDataException("请选择有效的 LUT / 色调映射分组。");
        return kind;
    }
    private static string FrameTags(JsonObject colors) => "setparams=" + string.Join(':',
        new[] { ("color_primaries", "color_primaries"), ("color_transfer", "color_trc"), ("color_space", "colorspace"), ("color_range", "range") }
        .Where(p => !JsonFields.Unspecified(colors.S(p.Item1))).Select(p => p.Item2 + "=" + (p.Item1 == "color_range" ? colors.S(p.Item1) switch { "pc" => "full", "tv" => "limited", var v => v } : colors.S(p.Item1))));
    private static string ToneMap(string range) => $"libplacebo=format=yuv444p16le:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range={range}:tonemapping=bt.2390:apply_dolbyvision=0,sidedata=delete:type=MASTERING_DISPLAY_METADATA,sidedata=delete:type=CONTENT_LIGHT_LEVEL,sidedata=delete:type=DYNAMIC_HDR_PLUS";
    // Expand sample range before the RGB LUT, retain float intermediates,
    // then convert the HLG RGB result to BT.2020 YCbCr. Quantize only at output.
    private static string LogFilter(JsonObject video, string profile)
    {
        var p = Profiles[profile];
        var matrix = video.S("color_space") switch
        {
            "gbr" => "gbr",
            "bt709" => "709",
            "bt2020nc" => "2020_ncl",
            "smpte170m" => "170m",
            "bt470bg" => "470bg",
            _ => p.Matrix
        };
        var range = video.S("color_range") switch
        {
            "pc" => "full",
            "tv" => "limited",
            _ => p.Range
        };
        return $"zscale=matrixin={matrix}:rangein={range}:range=full,format=gbrpf32le,lut3d=file={p.File}:interp=tetrahedral,setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:colorspace=gbr,zscale=matrix=2020_ncl:rangein=full:range=limited:dither=error_diffusion,format=yuv444p16le";
    }
    private static double Fps(JsonObject v) => new[] { v.S("avg_frame_rate"), v.S("r_frame_rate") }.Select(s => JsonFields.Number(s)).FirstOrDefault(n => n > 0, 30);
    // ffprobe reports rational SI values. x265 expects chromaticity in
    // 1/50000 and luminance in 1/10000 cd/m²; SVT-AV1 accepts decimal values.
    private static string[] HdrMetadata(IEnumerable<JsonObject> side, string codec)
    {
        var entries = side.ToArray();
        var md = entries.FirstOrDefault(s => s.S("side_data_type") == "Mastering display metadata");
        var cl = entries.FirstOrDefault(s => s.S("side_data_type") == "Content light level metadata");
        string[] fields = ["green_x", "green_y", "blue_x", "blue_y", "red_x", "red_y", "white_point_x", "white_point_y", "max_luminance", "min_luminance"];
        var result = new List<string>();
        if (md is not null && fields.All(md.ContainsKey))
        {
            var values = fields.Select((f, i) => codec == "hevc" ? N(Math.Round(JsonFields.Number(md.S(f)) * (i < 8 ? 50000 : 10000))) : JsonFields.Number(md.S(f)).ToString("F8", CultureInfo.InvariantCulture)).ToArray();
            result.Add((codec == "hevc" ? "master-display" : "mastering-display") + $"=G({values[0]},{values[1]})B({values[2]},{values[3]})R({values[4]},{values[5]})WP({values[6]},{values[7]})L({values[8]},{values[9]})");
        }
        if (cl is not null && cl.ContainsKey("max_content") && cl.ContainsKey("max_average"))
            result.Add((codec == "hevc" ? "max-cll" : "content-light") + $"={cl.S("max_content")},{cl.S("max_average")}");
        return result.ToArray();
    }
    /// <summary>Builds a shell-free command, resolved options and expected output color tags.</summary>
    /// <param name="seconds">Optional positive duration in seconds, measured from the start of the input.</param>
    public EncodingPlan Plan(MediaTools tools, MediaInfo media, EncodeOptions options, string destination, double? seconds = null)
    {
        Require(seconds is null || double.IsFinite(seconds.Value) && seconds.Value > 0, "--duration 必须为有限正数。");
        var kind = Validate(options, media);
        var o = ResolveFormat(options, media);
        var video = media.Video.DeepClone().AsObject();
        video["color_range"] = OutputPolicy.SourceRange(media, kind == "log" ? Profiles[o.Profile!].Range : null) == "full" ? "pc" : "tv";
        if (Hdr(kind))
        {
            video["color_transfer"] = kind == "hlg" ? "arib-std-b67" : "smpte2084";
            foreach (var p in Hlg())
                if (JsonFields.Unspecified(video.S(p.Key)))
                    video[p.Key] = p.Value?.DeepClone();
        }
        else if (kind == "sdr")
            foreach (var p in Rec709())
                if (JsonFields.Unspecified(video.S(p.Key)))
                    video[p.Key] = p.Value?.DeepClone();
        if (OutputPolicy.IsRgb(media))
            video["color_space"] = "gbr";
        var colors = o.Output == "sdr" ? Rec709() : kind == "log" ? Hlg() : new JsonObject();
        if (colors.Count == 0)
            foreach (var p in colorFlags)
                colors[p.Key] = video[p.Key]?.DeepClone();
        if (colors.S("color_space") == "gbr")
            colors["color_space"] = o.Output == "hdr" ? "bt2020nc" : "bt709";
        colors["color_range"] = o.ColorRange == "full" ? "pc" : "tv";
        var dynamic = media.SideData.Select(s => s.S("side_data_type")).Where(s => new[] { "DOVI", "Dolby Vision", "HDR Dynamic" }.Any(s.Contains)).ToArray();
        Require(dynamic.Length == 0 || o.Transform == "hdr-sdr" && dynamic.All(s => s.Contains("HDR10+")), "该素材带动态 HDR 元数据。HDR10+ 可选 HDR → SDR，Dolby Vision 需要专门流程。");
        var config = (o.Variant == "fast" ? Config["archive_fast_av1"] : Config[o.Purpose]?[o.Codec])!.AsObject();
        bool colorConversion = colors.Any(p => video.S(p.Key) != p.Value?.ToString());
        // A QSV surface can stay on the GPU only when the filter chain accepts
        // it. LUTs, tone mapping and software encoders need system-memory frames.
        bool softwareFrames = o.Transform != "none" || o.Purpose == "archive" || o.CpuDecode || colorConversion;
        var decoder = video.S("codec_name") switch
        {
            "h264" => "h264_qsv",
            "hevc" => "hevc_qsv",
            "av1" => "av1_qsv",
            _ => null
        };
        bool gpu = !softwareFrames && decoder is not null && video.S("pix_fmt") is "yuv420p" or "yuv420p10le" or "p010le";
        var args = new List<string> { tools.Ffmpeg, "-hide_banner", "-nostdin", "-n", "-xerror", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" };
        if (gpu)
            args.AddRange(["-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-c:v", decoder!]);
        args.AddRange(["-i", media.Path, "-map", "0:" + video.S("index"), "-map", "0:a?", "-map_metadata", o.Transform.StartsWith("log-") ? "-1" : "0", "-map_chapters", "0", "-sn", "-dn"]);
        var filters = new List<string>();
        if (kind == "log")
        {
            if (OutputPolicy.IsRgb(media))
                filters.Add("format=gbrpf32le");
            filters.Add(LogFilter(video, o.Profile!));
        }
        else if (!gpu)
        {
            if (OutputPolicy.IsRgb(media))
                filters.Add("format=" + (media.Pixel["flags"].B("float") ? "gbrpf32le" : "gbrp16le"));
            filters.Add(FrameTags(video));
        }
        if (o.Transform is "hdr-sdr" or "log-sdr")
            filters.Add(ToneMap(o.ColorRange == "full" ? "pc" : "tv"));
        if (gpu)
        {
            var colorOptions = ":out_range=" + colors.S("color_range");
            if (o.Output == "sdr")
                colorOptions += ":out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709";
            filters.Add($"vpp_qsv=format={(o.Depth == 10 ? "p010le" : "nv12")}{colorOptions}");
        }
        else
        {
            var conversion = new List<string> { "range=" + o.ColorRange, "dither=error_diffusion" };
            foreach (var (key, target) in new[] { ("color_primaries", "primaries"), ("color_transfer", "transfer"), ("color_space", "matrix") })
                if (!JsonFields.Unspecified(colors.S(key)))
                    conversion.Add(target + "=" + colors.S(key));
            filters.Add("zscale=" + string.Join(':', conversion));
            filters.Add("format=" + (o.Purpose == "share" && o.Depth == 10 ? "p010le" : o.PixelFormat));
            filters.Add(FrameTags(colors));
        }
        args.AddRange(["-vf", string.Join(',', filters), "-c:v", config.S("encoder"), "-preset", config.S("preset")]);
        if (o.Purpose == "share")
        {
            args.AddRange(["-global_quality:v", config.S("quality"), "-low_power", "1", "-async_depth", config.S("async_depth", "4"), "-g", N(Math.Max(1, Math.Round(2 * Fps(video))))]);
            if (o.Codec == "hevc")
                args.AddRange(["-profile:v", o.Depth == 10 ? "main10" : "main"]);
        }
        else
        {
            args.AddRange(["-crf", config.S("quality"), "-pix_fmt", o.PixelFormat, "-g", N(Math.Max(1, Math.Round(8 * Fps(video))))]);
            var extra = new JsonObject();
            if (o.Codec == "hevc")
            {
                extra["pools"] = 16;
                extra["log-level"] = "warning";
            }
            else if (o.Codec == "av1")
            {
                extra["lp"] = config["lp"]?.DeepClone() ?? JsonValue.Create(0);
                extra["tune"] = config["tune"]?.DeepClone() ?? JsonValue.Create(0);
            }
            foreach (var p in config["encoder_parameters"] as JsonObject ?? new())
                extra[p.Key] = p.Value?.DeepClone();
            var metadata = o.Output == "hdr" && kind == "pq" && o.Codec is "hevc" or "av1" ? HdrMetadata(media.SideData, o.Codec) : [];
            if (o.Codec == "hevc" && metadata.Length > 0)
            {
                extra["hdr10"] = 1;
                extra["hdr10-opt"] = 1;
                extra["repeat-headers"] = 1;
            }
            if (extra.Count > 0 || o.Codec is "hevc" or "av1")
                args.AddRange([o.Codec switch { "hevc" => "-x265-params", "av1" => "-svtav1-params", _ => "-x264-params" },
                    string.Join(':', extra.Select(p => p.Key + "=" + p.Value).Concat(metadata))]);
        }
        foreach (var (key, flag) in colorFlags)
            if (!JsonFields.Unspecified(colors.S(key)))
                args.AddRange([flag, colors.S(key)]);
        args.AddRange(["-fps_mode:v", "passthrough", "-enc_time_base:v", "filter"]);
        var audio = ResolveAudio(o);
        if (audio == "copy")
            args.AddRange(["-c:a", "copy"]);
        else if (audio == "flac")
        {
            foreach (var (track, index) in OutputPolicy.FlacTracks(media).Select((t, i) => (t, i)))
            {
                args.AddRange([$"-c:a:{index}", "flac", $"-compression_level:a:{index}", "8", $"-sample_fmt:a:{index}", track.Depth <= 16 ? "s16" : "s32", $"-bits_per_raw_sample:a:{index}", track.Depth.ToString(CultureInfo.InvariantCulture)]);
                if (track.Depth > 24)
                    args.AddRange([$"-strict:a:{index}", "experimental"]);
            }
        }
        else
        {
            var policy = Config["audio"]![audio]!;
            args.AddRange(["-c:a", policy.S("encoder"), "-b:a", policy.S("bitrate_kbps") + "k", "-ac", policy.S("channels")]);
            if (audio == "opus")
                args.AddRange(["-ar", policy.S("sample_rate"), "-vbr", policy.S("vbr"), "-application:a", policy.S("application"), "-compression_level:a", policy.S("compression_level")]);
        }
        if (Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            if (o.Codec == "hevc")
                args.AddRange(["-tag:v", "hvc1"]);
            args.AddRange(["-movflags", "+faststart+write_colr"]);
        }
        if (seconds is not null)
            args.AddRange(["-t", N(seconds.Value)]);
        args.Add(Path.GetFullPath(destination));
        return new(args.ToArray(), colors, o);
    }
    private string ResolveAudio(EncodeOptions o) => o.Audio == "auto" ? o.Purpose == "archive" ? Config["audio"].S("archive_default") : Config["audio"]!["share_default"].S(o.Codec) : o.Audio;
    /// <summary>Checks the selected FFmpeg build for required encoders, formats and filters.</summary>
    /// <remarks>Hardware availability is determined when FFmpeg initializes the actual job.</remarks>
    public async Task CheckRequirementsAsync(MediaTools tools, EncodeOptions o, EncodingPlan plan, CancellationToken cancellation)
    {
        string encoder = Config[o.Purpose]![o.Codec].S("encoder");
        var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"], cancellation, 15);
        bool Has(string text, string name) => Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b");
        Require(result.ExitCode == 0 && Has(result.Output, encoder), $"选中的 FFmpeg 缺少 {encoder} 编码器，请在工具路径里选择完整版本。");
        Require(o.Audio != "flac" || Has(result.Output, "flac"), "选中的 FFmpeg 缺少 FLAC 编码器。");
        if (o.Purpose == "archive")
        {
            result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-h", "encoder=" + encoder], cancellation, 15);
            var supported = Regex.Match(result.Output, @"Supported pixel formats:\s*([^\r\n]+)");
            Require(!supported.Success || supported.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(o.PixelFormat), $"当前 FFmpeg 的 {encoder} 不支持 {o.PixelFormat}，请更换编码器或采样格式。");
        }
        var filter = plan.Arguments[Array.IndexOf(plan.Arguments, "-vf") + 1];
        var required = new[] { "zscale", "lut3d", "libplacebo" }.Where(n => filter.Contains(n + "=")).ToArray();
        if (required.Length > 0)
        {
            result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-filters"], cancellation, 15);
            var missing = required.Order().Where(n => !Has(result.Output, n)).ToArray();
            Require(result.ExitCode == 0 && missing.Length == 0, "选中的 FFmpeg 缺少所需滤镜 " + string.Join(", ", missing) + "，请选择完整版本。");
        }
    }
    /// <summary>Probes the encoded file before publication and checks the requested format and metadata.</summary>
    public async Task<MediaInfo> VerifyAsync(MediaTools tools, MediaInfo source, EncodeOptions o, JsonObject expected, string output, CancellationToken cancellation = default)
    {
        o = ResolveFormat(o, source);
        var result = await MediaProbe.InspectAsync(tools, output, cancellation);
        Require(result.Video.S("pix_fmt").Replace("yuvj", "yuv") == o.PixelFormat || o.Sampling == "gray" && result.Video.B("_monochrome"), $"输出像素格式与选择不符，得到 {result.Video.S("pix_fmt")}。");
        foreach (var p in expected)
            Require(JsonFields.Unspecified(p.Value?.ToString()) || result.Video.S(p.Key) == p.Value?.ToString(), $"输出色彩标签不符：{p.Key}。");
        if (o.Audio == "flac")
        {
            var before = OutputPolicy.FlacTracks(source);
            var after = result.Audio.ToArray();
            Require(before.Length == after.Length && before.Select((p, i) => after[i].S("codec_name") == "flac" && after[i].S("sample_rate") == p.Stream.S("sample_rate")
                && after[i].I("channels") == p.Stream.I("channels") && (JsonFields.Unspecified(p.Stream.S("channel_layout")) || after[i].S("channel_layout") == p.Stream.S("channel_layout")) && after[i].I("bits_per_raw_sample") >= p.Depth).All(v => v),
                "FLAC 输出的音轨、采样率、声道或位深与原片不符。");
        }
        Require(o.Output != "sdr" || !result.SideData.Any(s => new[] { "HDR", "Dolby", "Mastering display metadata", "Content light level metadata" }.Any(s.S("side_data_type").Contains)), "SDR 输出残留 HDR 元数据。");
        if (o.Output == "hdr" && o.Transform == "none")
        {
            foreach (var before in source.SideData.Where(s => s.S("side_data_type") is "Mastering display metadata" or "Content light level metadata"))
            {
                var after = result.SideData.FirstOrDefault(s => s.S("side_data_type") == before.S("side_data_type"));
                Require(after is not null, "输出缺少源文件的 HDR 静态元数据。");
                foreach (var field in before.Where(p => p.Key != "side_data_type"))
                {
                    // Compare within one target bitstream quantization step;
                    // HEVC and AV1 store these metadata fields at different precision.
                    double tolerance = before.S("side_data_type") == "Content light level metadata" ? 0
                        : result.Video.S("codec_name") == "av1" ? field.Key switch
                        {
                            "max_luminance" => 1d / 256,
                            "min_luminance" => 1d / 16384,
                            _ => 1d / 65536
                        }
                        : field.Key.EndsWith("luminance") ? 1d / 10000 : 1d / 50000;
                    Require(after!.ContainsKey(field.Key) && Math.Abs(JsonFields.Number(field.Value?.ToString()) - JsonFields.Number(after.S(field.Key))) <= tolerance + 1e-12, "输出 HDR 元数据超出目标编码的精度容差。");
                }
            }
        }
        return result;
    }
    /// <summary>Encodes, verifies and atomically publishes a result; cancellation removes only job-owned files.</summary>
    /// <remarks>Existing outputs are replaced only after successful verification when overwrite is true.</remarks>
    public async Task<MediaInfo> RunAsync(MediaTools tools, MediaInfo media, EncodeOptions options, string output,
        Action<string, JsonNode?> emit, CancellationToken cancellation = default, double? seconds = null, bool overwrite = false)
    {
        output = Path.GetFullPath(output);
        Require(!ProcessRunner.SameFile(output, media.Path), "不能将源文件用作输出。");
        Require(!File.Exists(output) && !Directory.Exists(output) || overwrite && File.Exists(output), "输出文件已存在，请更换文件名。");
        var parent = Path.GetDirectoryName(output)!;
        Require(Directory.Exists(parent) && Path.GetExtension(output).Equals(options.Suffix, StringComparison.OrdinalIgnoreCase), $"请选择已有的输出目录，文件后缀应为 {options.Suffix}。");
        Validate(options, media);
        var o = ResolveFormat(options, media);
        // Keep the temporary result on the output volume so the final move is
        // atomic. A failed/cancelled encode never truncates an existing output.
        var scratch = Path.Combine(parent, ".video-converter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var temporary = Path.Combine(scratch, Path.GetFileName(output));
            var plan = Plan(tools, media, o, temporary, seconds);
            await CheckRequirementsAsync(tools, o, plan, cancellation);
            if (o.Suffix == ".mp4" && ResolveAudio(o) == "copy" && media.Audio.Any())
            {
                emit("status", JsonValue.Create("正在检查 MP4 音轨兼容性…"));
                ProcessResult trial;
                try
                {
                    trial = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-n", "-i", media.Path, "-map", "0:a", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-c:a", "copy", "-t", "0.08", "-f", "mp4", Path.Combine(scratch, "audio-compatibility.mp4")], cancellation, 20);
                }
                catch (TimeoutException) { throw new InvalidDataException("MP4 原音轨兼容性检测超时。请选择 AAC 音轨或 MKV 封装。"); }
                if (trial.ExitCode != 0)
                {
                    emit("log", JsonValue.Create(trial.Error.Trim()));
                    throw new InvalidDataException($"当前 FFmpeg 无法将原音轨（{string.Join(", ", media.Audio.Select(s => s.S("codec_name", "未知")).Distinct().Order())}）直接封装为 MP4。请选择 AAC 音轨，或改用 MKV 封装。");
                }
            }
            emit("log", JsonValue.Create(string.Join(' ', plan.Arguments.Select(a => '"' + a.Replace("\"", "\\\"") + '"'))));
            cancellation.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = ProcessRunner.StartInfo(plan.Arguments[0], plan.Arguments.Skip(1), Assets) };
            process.Start();
            using var registration = cancellation.Register(() => ProcessRunner.Stop(process));
            var eventLock = new object();
            void Send(string type, JsonNode? value)
            {
                lock (eventLock)
                    emit(type, value);
            }
            async Task ReadErrors()
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                        if (line.Trim() is { Length: > 0 } text)
                            Send("log", JsonValue.Create(text));
                }
                catch { ProcessRunner.Stop(process); throw; }
            }
            async Task ReadProgress()
            {
                try
                {
                    var progress = new JsonObject();
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        var pair = line.Trim().Split('=', 2);
                        if (pair.Length == 2 && pair[0] is "frame" or "out_time_us" or "speed" or "fps")
                            progress[pair[0]] = pair[1].Trim();
                        else if (pair[0] == "progress")
                        {
                            progress["duration"] = seconds is { } limit && media.Duration > 0 ? Math.Min(limit, media.Duration) : seconds ?? media.Duration;
                            Send("progress", progress.DeepClone());
                        }
                        else if (pair.Length == 1 && line.Trim().Length > 0)
                            Send("log", JsonValue.Create(line.Trim()));
                    }
                }
                catch { ProcessRunner.Stop(process); throw; }
            }
            // Both redirected pipes must be drained concurrently. Waiting for
            // the process first could deadlock when either OS pipe buffer fills.
            try
            {
                await Task.WhenAll(ReadProgress(), ReadErrors(), process.WaitForExitAsync());
            }
            finally { ProcessRunner.Stop(process); await process.WaitForExitAsync(); }
            cancellation.ThrowIfCancellationRequested();
            Require(process.ExitCode == 0, $"FFmpeg 转码失败（{process.ExitCode}）。请查看日志，或改用软件归档 / 软件解码。");
            emit("status", JsonValue.Create("正在核对输出色彩和采样格式…"));
            var result = await VerifyAsync(tools, media, o, plan.Colors, temporary, cancellation);
            cancellation.ThrowIfCancellationRequested();
            // Recheck cancellation immediately before the only operation that
            // can replace an existing destination.
            File.Move(temporary, output, overwrite);
            return result;
        }
        finally
        {
            if (Path.GetDirectoryName(scratch) != parent || !Path.GetFileName(scratch).StartsWith(".video-converter-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary directory.");
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }
}
