// Reference behavior from the pre-migration core; compiled only by differential checks.
using System.Globalization;
using System.Text.Json.Nodes;
using VideoConverter.Core;

namespace LumaRecode.Tests.Reference;

/// <summary>Resolves encoding policy, builds FFmpeg arguments and publishes verified output files.</summary>
/// <remarks>Planning has no process or filesystem side effects. Execution uses a sibling
/// temporary directory and only moves a verified result to its final destination.</remarks>
public sealed partial class ReferencePlanner
{
    private static string MotionScaleFilter(string size)
    {
        int longSide = size == "1080" ? 1920 : 1280, shortSide = size == "1080" ? 1080 : 720;
        return $"scale=w='min(iw,if(gte(iw,ih),{longSide},{shortSide}))':h='min(ih,if(gte(iw,ih),{shortSide},{longSide}))':force_original_aspect_ratio=decrease:force_divisible_by=2";
    }
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
    private static JsonObject Rec709() => new() { ["color_primaries"] = "bt709", ["color_transfer"] = "bt709", ["color_space"] = "bt709", ["color_range"] = "tv" };
    private static JsonObject Hlg() => new() { ["color_primaries"] = "bt2020", ["color_transfer"] = "arib-std-b67", ["color_space"] = "bt2020nc", ["color_range"] = "tv" };
    private static readonly (string Key, string Flag)[] colorFlags = [("color_primaries", "-color_primaries"), ("color_transfer", "-color_trc"), ("color_space", "-colorspace"), ("color_range", "-color_range")];
    private static bool IsHdr(string kind) => kind is "hlg" or "pq";
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
    private static string FormatNumber(double value) => value.ToString("0.#################", CultureInfo.InvariantCulture);
    public ReferencePlanner(string assets)
    {
        Assets = Path.GetFullPath(assets);
        Config = JsonNode.Parse(File.ReadAllText(Path.Combine(Assets, "recommended-presets.json")))!.AsObject();
        motionConfig = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(Assets, "motion-photo-presets.json")))!.AsObject());
    }
    /// <summary>Resolves source-dependent defaults and enforces codec, sampling and depth limits.</summary>
    /// <remarks>Call before using PixelFormat or Depth; their values must be concrete.</remarks>
    public EncodeOptions ResolveFormat(EncodeOptions options, MediaInfo media)
    {
        Require(options.Purpose is "share" or "archive" && options.Codec is "hevc" or "av1" or "avc", "请选择有效的用途和编码器。");
        Require(options.Output != "hdr" || options.Codec != "avc", "HDR 输出请选择 HEVC 或 AV1。");
        Require(options.Sampling is "auto" or "source" or "420" or "422" or "444" or "gray", "请选择自动、保持原采样、4:2:0、4:2:2 或 4:4:4。");
        var sampling = options.Sampling == "auto" ? ReferenceOutputPolicy.DefaultSampling(media, options.Purpose, options.Codec) : options.Sampling;
        if (sampling == "source")
        {
            Require(!media.Pixel["flags"].Flag("alpha"), "素材含透明通道。请选择具体采样格式，输出为不带透明通道的视频。");
            sampling = ReferenceOutputPolicy.SourceSampling(media);
        }
        Require(sampling is "420" or "422" or "444" or "gray", "当前编码器无法保持原采样，请手动选择 4:2:0、4:2:2 或 4:4:4。");
        Require(ReferenceOutputPolicy.SamplingChoices(media).Contains(sampling), "输出采样须在原片采样上限内选择。");
        Require(sampling == "420" || options.Purpose == "archive" && options.Codec != "av1", "保持此采样需要软件 HEVC 或 H.264；也可手动选择 4:2:0。");
        Require(options.ColorRange is "auto" or "source" or "limited" or "full", "请选择自动、保持原范围、Limited 或 Full。");
        var range = options.ColorRange == "auto" ? options.Purpose == "archive" ? "source" : "limited" : options.ColorRange;
        if (range == "source")
        {
            range = ReferenceOutputPolicy.SourceRange(media, options.Transform.StartsWith("log-") && Profiles.TryGetValue(options.Profile ?? "", out var p) ? p.Range : null);
        }

        Require(options.BitDepth is "auto" or "8" or "10" or "12", "请选择自动、8、10 或 12 位输出。");
        var depths = ReferenceOutputPolicy.DepthChoices(media, options.Purpose, options.Codec, options.Output);
        Require(depths.Length > 0, "HDR 输出需要至少 10 位的源素材，请选择 SDR 或高位深原片。");
        var depth = options.BitDepth == "auto" ? ReferenceOutputPolicy.DefaultDepth(media, options.Purpose, options.Codec, options.Output)!.Value : int.Parse(options.BitDepth, CultureInfo.InvariantCulture);
        Require(depths.Contains(depth), "输出位深须在原片和编码器的位深上限内选择；HDR 至少需要 10 位。");
        return options with
        {
            Sampling = sampling,
            ColorRange = range,
            BitDepth = depth.ToString(CultureInfo.InvariantCulture)
        };
    }
    /// <summary>Checks transform/source compatibility and returns the effective input kind.</summary>
    /// <remarks>A manual SourceKind overrides detection; a Log profile is only valid for Log transforms.</remarks>
    public string Validate(EncodeOptions options, MediaInfo media)
    {
        options = ResolveFormat(options, media);
        Require(options.Container is "auto" or "mp4" or "mkv", "请选择自动、MP4 或 MKV 封装。");
        Require(options.Output is "sdr" or "hdr", "请选择 SDR 或 HDR 输出。");
        Require(options.Audio is "auto" or "copy" or "aac" or "opus" or "flac", "请选择有效的音轨格式。");
        if (options.Audio == "flac")
        {
            Require(options.Container != "mp4", "FLAC 音轨请选择 MKV 封装，以保留声道布局。");
            ReferenceOutputPolicy.FlacTracks(media);
        }
        Require(options.Variant == "standard" || options.Variant == "fast" && options.Purpose == "archive" && options.Codec == "av1", "快速备用质量档仅用于软件归档 AV1。");
        var kind = options.SourceKind == "auto" ? media.Kind : options.SourceKind;
        Require(kind is "sdr" or "hlg" or "pq" or "log" or "raw" or "unknown", "请选择有效的输入类型。");
        Require((kind == "raw") == (media.Raw is not null), "ProRes RAW 使用自动识别与相机参数显影。");
        Require(media.Raw is null || media.Raw.Flag("decoder_version_supported"), "ProRes RAW 显影需要 FFmpeg 9 或更新版本。");
        Require(media.Raw is null || media.Raw.Flag("decoder_supported"), "当前 FFmpeg 无法解码此 ProRes RAW。请先用支持该相机的 RAW 软件显影为 ProRes 或 HEVC。");
        if (media.Raw is not null)
        {
            RawDevelopment.Orientation(media);
        }
        DynamicHdr.Validate(media, options);
        if (options.Transform is "log-hdr" or "log-sdr")
        {
            Require(Profiles.ContainsKey(options.Profile ?? ""), "请选择具体的 Log 曲线。");
            Require(options.Transform == (options.Output == "hdr" ? "log-hdr" : "log-sdr"), "Log 转换目标需要与 SDR/HDR 输出一致。");
            Require(kind == "log", "Log 转换需要将输入解释指定为 Log。");
            Require(ReferenceOutputPolicy.SourceDepth(media) >= 10, "这些 Log 转换需要对应相机的高位深原片。");
            return "log";
        }
        if (options.Transform == "hdr-sdr")
        {
            Require(options.Output == "sdr" && IsHdr(kind), "HDR → SDR 需要 HLG/PQ 输入及 SDR 输出。");
        }
        else if (options.Transform == "none")
        {
            Require(kind is not ("log" or "unknown"), "请确认输入类型。Log 原片需要选择曲线后转换。");
            Require(options.Output != "hdr" || kind != "sdr", "SDR 输入请选择 SDR 输出。");
            Require(options.Output != "sdr" || !IsHdr(kind), "HDR 输入输出 SDR 时，请选择 HDR → SDR 转换。");
        }
        else
        {
            throw new InvalidDataException("请选择有效的 LUT / 色调映射分组。");
        }

        return kind;
    }
    private static string FrameTags(JsonObject colors) => "setparams=" + string.Join(':',
        new[] { ("color_primaries", "color_primaries"), ("color_transfer", "color_trc"), ("color_space", "colorspace"), ("color_range", "range") }
        .Where(p => !JsonFields.Unspecified(colors.Text(p.Item1))).Select(p => p.Item2 + "=" + (p.Item1 == "color_range" ? colors.Text(p.Item1) switch { "pc" => "full", "tv" => "limited", var v => v } : colors.Text(p.Item1))));
    private static string ToneMap(string range, bool dolby = false) => $"libplacebo=format=yuv444p16le:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range={range}:tonemapping=bt.2390:apply_dolbyvision={(dolby ? 1 : 0)},sidedata=delete:type=MASTERING_DISPLAY_METADATA,sidedata=delete:type=CONTENT_LIGHT_LEVEL,sidedata=delete:type=DYNAMIC_HDR_PLUS";
    // Expand sample range before the RGB LUT, retain float intermediates,
    // then convert the HLG RGB result to BT.2020 YCbCr. Quantize only at output.
    private static string LogFilter(JsonObject video, string profile)
    {
        var p = Profiles[profile];
        var matrix = video.Text("color_space") switch
        {
            "gbr" => "gbr",
            "bt709" => "709",
            "bt2020nc" => "2020_ncl",
            "smpte170m" => "170m",
            "bt470bg" => "470bg",
            _ => p.Matrix
        };
        var range = video.Text("color_range") switch
        {
            "pc" => "full",
            "tv" => "limited",
            _ => p.Range
        };
        return $"zscale=matrixin={matrix}:rangein={range}:range=full,format=gbrpf32le,lut3d=file={p.File}:interp=tetrahedral,setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:colorspace=gbr,zscale=matrix=2020_ncl:rangein=full:range=limited:dither=error_diffusion,format=yuv444p16le";
    }
    private static double Fps(JsonObject v) => new[] { v.Text("avg_frame_rate"), v.Text("r_frame_rate") }.Select(s => JsonFields.Number(s)).FirstOrDefault(n => n > 0, 30);
    // ffprobe reports rational SI values. x265 expects chromaticity in
    // 1/50000 and luminance in 1/10000 cd/m²; SVT-AV1 accepts decimal values.
    private static string[] HdrMetadata(IEnumerable<JsonObject> side, string codec)
    {
        var entries = side.ToArray();
        var md = entries.FirstOrDefault(s => s.Text("side_data_type") == "Mastering display metadata");
        var cl = entries.FirstOrDefault(s => s.Text("side_data_type") == "Content light level metadata");
        string[] fields = ["green_x", "green_y", "blue_x", "blue_y", "red_x", "red_y", "white_point_x", "white_point_y", "max_luminance", "min_luminance"];
        var result = new List<string>();
        if (md is not null && fields.All(md.ContainsKey))
        {
            var values = fields.Select((f, i) => codec == "hevc" ? FormatNumber(Math.Round(JsonFields.Number(md.Text(f)) * (i < 8 ? 50000 : 10000))) : JsonFields.Number(md.Text(f)).ToString("F8", CultureInfo.InvariantCulture)).ToArray();
            result.Add((codec == "hevc" ? "master-display" : "mastering-display") + $"=G({values[0]},{values[1]})B({values[2]},{values[3]})R({values[4]},{values[5]})WP({values[6]},{values[7]})L({values[8]},{values[9]})");
        }
        if (cl is not null && cl.ContainsKey("max_content") && cl.ContainsKey("max_average"))
        {
            result.Add((codec == "hevc" ? "max-cll" : "content-light") + $"={cl.Text("max_content")},{cl.Text("max_average")}");
        }

        return result.ToArray();
    }
    /// <summary>Builds a shell-free command, resolved options and expected output color tags.</summary>
    /// <param name="seconds">Optional positive duration in seconds, measured from the start of the input.</param>
    /// <param name="previewSize">Zero for delivery; a positive viewport limit scales before expensive color rendering.</param>
    public EncodingPlan Plan(MediaTools tools, MediaInfo media, EncodeOptions requestedOptions, string destination, double? seconds = null,
        FrameWindow? window = null, bool motionPhoto = false, string motionSize = "source", int previewSize = 0)
        => BuildPlan(tools, media, requestedOptions, destination, seconds, forceQsvTimingCompatibility: false, window: window, motionPhoto: motionPhoto, motionSize: motionSize, previewSize: previewSize);

    private EncodingPlan BuildPlan(MediaTools tools, MediaInfo media, EncodeOptions requestedOptions, string destination, double? seconds,
        bool forceQsvTimingCompatibility, bool autoLowPower = false, FrameWindow? window = null, bool motionPhoto = false, string motionSize = "source", int previewSize = 0)
    {
        Require(seconds is null || double.IsFinite(seconds.Value) && seconds.Value > 0, "--duration 必须为有限正数。");
        ArgumentOutOfRangeException.ThrowIfNegative(previewSize);
        Require(!motionPhoto || motionSize is "source" or "1080" or "720", "请选择有效的动态视频分辨率。");
        var kind = Validate(requestedOptions, media);
        var options = ResolveFormat(FrameOptions(media, requestedOptions, window is not null, motionPhoto), media);
        var video = media.Video.DeepClone().AsObject();
        video["color_range"] = ReferenceOutputPolicy.SourceRange(media, kind == "log" ? Profiles[options.Profile!].Range : null) == "full" ? "pc" : "tv";
        if (IsHdr(kind))
        {
            video["color_transfer"] = kind == "hlg" ? "arib-std-b67" : "smpte2084";
            foreach (var p in Hlg())
            {
                if (JsonFields.Unspecified(video.Text(p.Key)))
                {
                    video[p.Key] = p.Value?.DeepClone();
                }
            }
        }
        else if (kind == "sdr")
        {
            foreach (var p in Rec709())
            {
                if (JsonFields.Unspecified(video.Text(p.Key)))
                {
                    video[p.Key] = p.Value?.DeepClone();
                }
            }
        }

        bool preserveDynamic = DynamicHdr.Preserve(media, options);
        bool developRaw = media.Raw is not null;
        bool renderDolby = media.DolbyVision is not null && !preserveDynamic;
        if (preserveDynamic && media.DolbyVision?.Integer("dv_profile") == 5)
        {
            video["color_space"] = "ipt-c2";
        }
        if (ReferenceOutputPolicy.IsRgb(media))
        {
            video["color_space"] = "gbr";
        }

        var colors = options.Output == "sdr" ? Rec709() : kind is "log" or "raw" ? Hlg() : new JsonObject();
        if (colors.Count == 0)
        {
            foreach (var (key, _) in colorFlags)
            {
                colors[key] = video[key]?.DeepClone();
            }
        }

        if (colors.Text("color_space") == "gbr")
        {
            colors["color_space"] = options.Output == "hdr" ? "bt2020nc" : "bt709";
        }

        colors["color_range"] = options.ColorRange == "full" ? "pc" : "tv";

        var config = (motionPhoto ? MotionConfig[options.Purpose]?[options.Codec] : options.Variant == "fast" ? Config["archive_fast_av1"] : Config[options.Purpose]?[options.Codec])!.AsObject();
        bool colorConversion = colors.Any(p => video.Text(p.Key) != p.Value?.ToString());
        // A QSV surface can stay on the GPU only when the filter chain accepts
        // it. LUTs, tone mapping and software encoders need system-memory frames.
        // FFmpeg's automatic display-matrix transform skips hardware surfaces.
        // Decode these inputs in software so rotations/mirroring are applied
        // to pixels once, rather than lost when the new stream is muxed.
        bool displayTransform = media.SideData.Any(s => s.Text("side_data_type") == "Display Matrix");
        bool softwareFrames = options.Transform != "none" || options.Purpose == "archive" || options.CpuDecode || colorConversion || displayTransform || media.DynamicHdr || developRaw || window is not null || motionPhoto;
        // Different nominal/average rates identify common camera VFR files.
        // Equal or absent rates do not prove CFR; execution can retry once if
        // the QSV runtime returns non-monotonic DTS despite these headers.
        var averageRate = JsonFields.Number(video.Text("avg_frame_rate"));
        var nominalRate = JsonFields.Number(video.Text("r_frame_rate"));
        bool timingCompatibility = options.Purpose == "share" && (forceQsvTimingCompatibility ||
            averageRate > 0 && nominalRate > 0 && Math.Abs(averageRate - nominalRate) > Math.Max(0.01, nominalRate * 0.001));
        if (timingCompatibility && options.Depth != ReferenceOutputPolicy.SourceDepth(media))
        {
            // VPP quantizes PTS to its nominal frame interval. Convert depth in
            // software for VFR; unchanged QSV surfaces can bypass VPP entirely.
            softwareFrames = true;
        }
        var decoder = video.Text("codec_name") switch
        {
            "h264" => "h264_qsv",
            "hevc" => "hevc_qsv",
            "av1" => "av1_qsv",
            _ => null
        };
        bool gpu = !softwareFrames && decoder is not null && video.Text("pix_fmt") is "yuv420p" or "yuv420p10le" or "p010le";
        var args = new List<string> { tools.Ffmpeg, "-hide_banner", "-nostdin", "-n", "-xerror", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" };
        if (gpu)
        {
            args.AddRange(["-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-c:v", decoder!]);
        }

        if (developRaw)
        {
            // FFmpeg's shared ProRes AVX2 coefficient permutation is incompatible
            // with the RAW Bayer C IDCT on affected builds (upstream #25461).
            args.AddRange(["-threads:v", "2", "-filter_threads", "2", .. PlatformDefaults.RawCpuArguments, "-apply_cropping", "0", "-noautorotate"]);
        }
        if (window is { SeekFrame: > 0 })
        {
            args.AddRange(["-seek_timestamp", "1", "-ss", FormatNumber(window.SeekSeconds)]);
        }
        // The 5.0 planner also limits demuxing to the selected local file.
        args.AddRange(["-protocol_whitelist", "file", "-format_whitelist",
            "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,mpegts,mpeg,mpegvideo,flv,asf,ogg,mxf,dv,h264,hevc,vvc,av1,ivf,vc1,dnxhd,rm,gif,avif,jpeg_pipe,png_pipe,webp_pipe",
            "-i", media.Path, "-map", "0:" + video.Text("index"), "-map", motionPhoto ? "0:a:0?" : "0:a?", "-map_metadata", motionPhoto || options.Transform.StartsWith("log-") ? "-1" : "0", "-map_chapters", motionPhoto || window is not null ? "-1" : "0", "-sn", "-dn"]);
        var filters = new List<string>();
        if (window is not null)
        {
            filters.Add($"trim=start_frame={window.Start - window.SeekFrame}:end_frame={window.End - window.SeekFrame}");
        }
        // Viewport previews reduce the working set before expensive LUT/tone
        // mapping. Delivery paths leave previewSize at zero and render in full.
        if (previewSize > 0 && !developRaw)
        {
            filters.Add(ScaleFilter(previewSize));
        }
        if (motionPhoto && !developRaw)
        {
            filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2");
        }
        if (developRaw)
        {
            filters.Add(RawDevelopment.Filter(media.Raw!, "raw-calibration.cmd", previewSize));
            filters.AddRange(RawDevelopment.Orientation(media));
            if (options.Sampling is "420" or "422")
            {
                filters.Add("pad=ceil(iw/2)*2:" + (options.Sampling == "420" ? "ceil(ih/2)*2" : "ih"));
            }
        }
        else if (preserveDynamic)
        {
            // In particular, Profile 5 stores IPTPQc2 code values. Passing these
            // through a YCbCr/RGB converter would invalidate its RPU metadata.
            filters.Add("null");
        }
        else if (renderDolby)
        {
            if (options.Output == "hdr")
            {
                filters.Add($"libplacebo=format=yuv444p16le:colorspace=bt2020nc:color_primaries=bt2020:color_trc={(kind == "hlg" ? "arib-std-b67" : "smpte2084")}:range={(options.ColorRange == "full" ? "pc" : "tv")}:apply_dolbyvision=1");
            }
        }
        else if (kind == "log")
        {
            if (ReferenceOutputPolicy.IsRgb(media))
            {
                filters.Add("format=gbrpf32le");
            }

            filters.Add(LogFilter(video, options.Profile!));
        }
        else if (!gpu)
        {
            if (ReferenceOutputPolicy.IsRgb(media))
            {
                filters.Add("format=" + (media.Pixel["flags"].Flag("float") ? "gbrpf32le" : "gbrp16le"));
            }

            filters.Add(FrameTags(video));
        }
        if (options.Transform is "hdr-sdr" or "log-sdr" || developRaw && options.Output == "sdr")
        {
            filters.Add(ToneMap(options.ColorRange == "full" ? "pc" : "tv", renderDolby));
        }

        if (media.DynamicHdr && !preserveDynamic)
        {
            filters.Add("sidedata=delete:type=DOVI_RPU_BUFFER,sidedata=delete:type=DOVI_METADATA,sidedata=delete:type=DYNAMIC_HDR_PLUS");
        }
        if (gpu)
        {
            if (timingCompatibility)
            {
                filters.Add(FrameTags(colors));
            }
            else
            {
                var colorOptions = ":out_range=" + colors.Text("color_range");
                if (options.Output == "sdr")
                {
                    colorOptions += ":out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709";
                }

                filters.Add($"vpp_qsv=format={(options.Depth == 10 ? "p010le" : "nv12")}{colorOptions}");
            }
        }
        else if (!preserveDynamic)
        {
            var conversion = new List<string> { "range=" + options.ColorRange, "dither=error_diffusion" };
            foreach (var (key, target) in new[] { ("color_primaries", "primaries"), ("color_transfer", "transfer"), ("color_space", "matrix") })
            {
                if (!JsonFields.Unspecified(colors.Text(key)))
                {
                    conversion.Add(target + "=" + colors.Text(key));
                }
            }

            filters.Add("zscale=" + string.Join(':', conversion));
            filters.Add("format=" + (options.Purpose == "share" && options.Depth == 10 ? "p010le" : options.PixelFormat));
            filters.Add(FrameTags(colors));
        }
        if (motionPhoto && motionSize != "source")
        {
            filters.Add(MotionScaleFilter(motionSize));
        }
        if (window is not null)
        {
            filters.Add("setpts=PTS-STARTPTS");
        }
        args.AddRange(["-vf", string.Join(',', filters), "-c:v", config.Text("encoder"), "-preset", config.Text("preset")]);
        if (options.Purpose == "share")
        {
            // Keep high-frame-rate inputs below the long-GOP range associated
            // with device failures on some Arc drivers (Intel article 000095403).
            args.AddRange(["-global_quality:v", config.Text("quality"), "-low_power", autoLowPower ? "-1" : "1", "-async_depth", config.Text("async_depth", "4"), "-g", FormatNumber(Math.Clamp(Math.Round(2 * Fps(video)), 1, 240))]);
            if (timingCompatibility && options.Codec is "avc" or "hevc")
            {
                // Some QSV runtimes derive reordered DTS from a fixed cadence.
                // I/P-only encoding preserves VFR PTS without that reordering.
                args.AddRange(["-bf:v", "0"]);
            }
            if (options.Codec == "hevc")
            {
                args.AddRange(["-profile:v", options.Depth == 10 ? "main10" : "main"]);
            }
        }
        else
        {
            args.AddRange(["-crf", config.Text("quality"), "-pix_fmt", options.PixelFormat, "-g", FormatNumber(Math.Max(1, Math.Round((motionPhoto ? 2 : 8) * Fps(video))))]);
            var extra = new JsonObject();
            if (options.Codec == "hevc")
            {
                extra["pools"] = 16;
                extra["log-level"] = "warning";
            }
            else if (options.Codec == "av1")
            {
                extra["lp"] = config["lp"]?.DeepClone() ?? JsonValue.Create(0);
                extra["tune"] = config["tune"]?.DeepClone() ?? JsonValue.Create(0);
            }
            foreach (var p in config["encoder_parameters"] as JsonObject ?? new())
            {
                extra[p.Key] = p.Value?.DeepClone();
            }

            if (preserveDynamic && media.DolbyVision is not null && options.Codec == "hevc")
            {
                extra["vbv-maxrate"] = 100000;
                extra["vbv-bufsize"] = 100000;
            }
            if (preserveDynamic && media.Hdr10Plus)
            {
                extra["dhdr10-info"] = "hdr10plus.json";
            }
            var metadata = options.Output == "hdr" && kind == "pq" && options.Codec is "hevc" or "av1" ? HdrMetadata(media.SideData, options.Codec) : [];
            if (options.Codec == "hevc" && metadata.Length > 0)
            {
                extra["hdr10"] = 1;
                extra["hdr10-opt"] = 1;
                extra["repeat-headers"] = 1;
            }
            if (extra.Count > 0 || options.Codec is "hevc" or "av1")
            {
                args.AddRange([options.Codec switch { "hevc" => "-x265-params", "av1" => "-svtav1-params", _ => "-x264-params" },
                    string.Join(':', extra.Select(p => p.Key + "=" + p.Value).Concat(metadata))]);
            }
        }
        if (media.DolbyVision is not null && options.Purpose == "archive" && options.Codec is "hevc" or "av1")
        {
            args.AddRange(["-dolbyvision", preserveDynamic ? "1" : "0"]);
        }
        if (preserveDynamic)
        {
            args.AddRange(["-strict", "unofficial"]);
        }
        foreach (var (key, flag) in colorFlags)
        {
            if (!JsonFields.Unspecified(colors.Text(key)) && !(preserveDynamic && media.DolbyVision?.Integer("dv_profile") == 5))
            {
                args.AddRange([flag, colors.Text(key)]);
            }
        }

        args.AddRange(["-fps_mode:v", "passthrough", "-enc_time_base:v", "filter"]);
        if (developRaw)
        {
            args.AddRange(["-metadata:s:v:0", "rotate=0"]);
        }
        var audio = ResolveAudio(options);
        var audioFilters = new List<string>();
        if (window is not null && media.Audio.Any())
        {
            double origin = window.SeekFrame > 0 ? window.SeekSeconds : JsonFields.Number(media.Video.Text("_format_start_time"));
            // Input seeking offsets timestamps on each audio stream's sample
            // clock. Restore that integer offset before trimming in source
            // time, avoiding one-sample errors at rational video boundaries.
            audioFilters.Add($"asetpts=PTS+round({FormatNumber(origin)}*SR)/SR/TB");
            audioFilters.Add($"atrim=start={FormatNumber(window.StartSeconds)}:end={FormatNumber(window.EndSeconds)}");
            audioFilters.Add($"asetpts=PTS-({FormatNumber(window.StartSeconds)})/TB");
        }
        if (motionPhoto && media.Audio.Any())
        {
            audioFilters.Add($"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts={(media.Audio.First().Integer("channels") == 1 ? "mono" : "stereo")}");
        }
        if (audioFilters.Count > 0)
        {
            args.AddRange(["-af", string.Join(',', audioFilters)]);
        }
        if (motionPhoto && media.Audio.Any())
        {
            var policy = MotionConfig["audio"]!;
            bool mono = media.Audio.First().Integer("channels") == 1;
            args.AddRange(["-c:a", policy.Text("encoder"), "-profile:a", policy.Text("profile"), "-b:a", policy.Text(mono ? "mono_bitrate_kbps" : "stereo_bitrate_kbps") + "k", "-ar", policy.Text("sample_rate"), "-ac", mono ? "1" : "2"]);
        }
        else if (audio == "copy")
        {
            args.AddRange(["-c:a", "copy"]);
        }
        else if (audio == "flac")
        {
            foreach (var (track, index) in ReferenceOutputPolicy.FlacTracks(media).Select((t, i) => (t, i)))
            {
                args.AddRange([$"-c:a:{index}", "flac", $"-compression_level:a:{index}", "8", $"-sample_fmt:a:{index}", track.Depth <= 16 ? "s16" : "s32", $"-bits_per_raw_sample:a:{index}", track.Depth.ToString(CultureInfo.InvariantCulture)]);
                if (track.Depth > 24)
                {
                    args.AddRange([$"-strict:a:{index}", "experimental"]);
                }
            }
        }
        else
        {
            var policy = Config["audio"]![audio]!;
            args.AddRange(["-c:a", policy.Text("encoder"), "-b:a", policy.Text("bitrate_kbps") + "k", "-ac", policy.Text("channels")]);
            if (audio == "opus")
            {
                args.AddRange(["-ar", policy.Text("sample_rate"), "-vbr", policy.Text("vbr"), "-application:a", policy.Text("application"), "-compression_level:a", policy.Text("compression_level")]);
            }
        }
        if (Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            if (options.Codec == "hevc")
            {
                args.AddRange(["-tag:v", "hvc1"]);
            }

            args.AddRange(["-movflags", "+faststart+write_colr"]);
        }
        if (seconds is not null)
        {
            args.AddRange(["-t", FormatNumber(seconds.Value)]);
        }

        args.Add(Path.GetFullPath(destination));
        return new(args.ToArray(), colors, options, timingCompatibility, developRaw || preserveDynamic && media.Hdr10Plus ? Path.GetDirectoryName(Path.GetFullPath(destination)) : null);
    }
    private string ResolveAudio(EncodeOptions options) => options.Audio == "auto" ? options.Purpose == "archive" ? Config["audio"].Text("archive_default") : Config["audio"]!["share_default"].Text(options.Codec) : options.Audio;
    private EncodeOptions FrameOptions(MediaInfo media, EncodeOptions options, bool trimmed, bool motionPhoto)
    {
        if (motionPhoto)
        {
            Require(options.Codec is "hevc" or "avc", "动态照片视频请选择 HEVC 或 AVC。");
            options = options with
            {
                Audio = "aac"
            };
        }
        if (trimmed && media.Audio.Any())
        {
            if (options.Audio == "auto" && ResolveAudio(options) == "copy")
            {
                options = options with
                {
                    Audio = options.Container != "mp4" && ReferenceOutputPolicy.FlacEligibility(media).Eligible ? "flac" : "aac"
                };
            }
            Require(ResolveAudio(options) != "copy", "逐帧裁剪需要重新编码音轨，请选择 AAC、Opus 或 FLAC。");
        }
        return options;
    }

    private static string ScaleFilter(int maximum) => $"scale=w='min(iw,{maximum})':h='min(ih,{maximum})':force_original_aspect_ratio=decrease:force_divisible_by=2";
}
