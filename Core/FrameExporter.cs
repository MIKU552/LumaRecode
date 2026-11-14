using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

/// <summary>Zero-based cover frame and an optional end-exclusive motion range on the source timeline.</summary>
public sealed record ImageExportOptions(int Frame = 0, string Format = "sdr", string SourceKind = "auto", string? Profile = null,
    bool Motion = false, FrameRange? MotionFrames = null, int Quality = 95, string Purpose = "archive", string MotionSize = "source")
{
    public string Suffix => Format == "hdr" ? ".avif" : Format == "png" ? ".png" : ".jpg";
}

public sealed record ImageExportResult(string Path, long Bytes, int Width, int Height, string Format, double MotionDuration);

/// <summary>Frame-accurate stills and Google JPEG Motion Photos, sharing the video's color pipeline.</summary>
public sealed class FrameExporter(Transcoder engine)
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string Kind(MediaInfo source, ImageExportOptions options) => options.SourceKind == "auto" ? source.Kind : options.SourceKind;

    /// <summary>Validates the image contract without creating files or starting a decoder.</summary>
    public static void ValidateOptions(MediaInfo source, ImageExportOptions options, string destination)
    {
        Require(options.Format is "hdr" or "ultrahdr" or "sdr" or "png", "请选择有效的图片格式。");
        Require(Path.GetExtension(destination).Equals(options.Suffix, StringComparison.OrdinalIgnoreCase), $"图片文件后缀应为 {options.Suffix}。");
        Require(!options.Motion || options.Format is "ultrahdr" or "sdr", "动态照片需要 JPEG 或 Ultra HDR JPEG。");
        Require(!options.Motion || Path.GetFileNameWithoutExtension(destination).EndsWith("MP", StringComparison.Ordinal), "Google 动态照片文件名须以 MP.jpg 结尾。");
        Require(options.Quality is >= 1 and <= 100, "图片质量须在 1 到 100 之间。");
        Require(options.Purpose is "share" or "archive", "请选择有效的编码用途。");
        Require(options.MotionSize is "source" or "1080" or "720", "请选择有效的动态视频分辨率。");
        string kind = Kind(source, options);
        Require(options.Format is "sdr" or "png" || kind is "hlg" or "pq" or "log" or "raw", "HDR 图片导出需要 HDR、Log 或 RAW 素材。");
        Require(kind != "log" || !string.IsNullOrWhiteSpace(options.Profile ?? source.Profile), "请选择相机曲线。");
    }

    internal static EncodeOptions RenderOptions(MediaInfo source, ImageExportOptions options, bool sdr, string purpose = "archive")
    {
        string kind = Kind(source, options);
        string? profile = options.Profile ?? source.Profile;
        int depth = sdr ? purpose == "share" ? 8 : Math.Clamp(OutputPolicy.SourceDepth(source), 8, 12) : Math.Min(12, Math.Max(10, OutputPolicy.SourceDepth(source)));
        // Video policy determines the intermediate sampling/depth. RGB and
        // image-container encoding are applied only after color rendering.
        return new(Purpose: purpose, Codec: sdr && purpose == "share" ? "avc" : "hevc", Output: sdr ? "sdr" : "hdr",
            Transform: kind == "log" ? sdr ? "log-sdr" : "log-hdr" : kind is "hlg" or "pq" && sdr ? "hdr-sdr" : "none",
            Profile: profile, Sampling: purpose == "share" ? "420" : OutputPolicy.DefaultSampling(source, "archive", "hevc"),
            Audio: "aac", SourceKind: options.SourceKind, Container: purpose == "share" ? "mp4" : "mkv", ColorRange: "full",
            BitDepth: depth.ToString(CultureInfo.InvariantCulture), DynamicHdr: "standard");
    }

    /// <summary>Centers a three-second clip around the cover, shifting it inward at the source boundaries.</summary>
    public static FrameRange DefaultMotionRange(FrameTimeline timeline, int frame)
    {
        double center = timeline.Position(frame);
        double start = Math.Max(0, Math.Min(center - 1.5, timeline.Duration - 3));
        int first = timeline.AtOrAfter(start);
        int last = timeline.AtOrAfter(Math.Min(timeline.Duration, start + 3));
        return new(first, Math.Min(timeline.Count, Math.Max(frame + 1, last)));
    }

    /// <summary>Renders a bounded SDR viewport frame without probing or writing an image file.</summary>
    public Task<byte[]> PreviewAsync(MediaTools tools, MediaInfo source, FrameTimeline timeline, ImageExportOptions options, CancellationToken cancellation = default)
        => new FramePreview(engine).RenderAsync(tools, source, timeline, options, cancellation);

    /// <summary>Renders a full-resolution still, optionally appends a verified movie, then publishes atomically.</summary>
    /// <remarks>The caller owns the source snapshot. A cached timeline is reused only while its file identity matches.</remarks>
    public async Task<ImageExportResult> ExportAsync(MediaTools tools, MediaInfo source, ImageExportOptions options, string destination,
        Action<string, JsonNode?> emit, bool overwrite = false, CancellationToken cancellation = default, FrameTimeline? timeline = null)
    {
        destination = Path.GetFullPath(destination);
        Require(!ProcessRunner.SameFile(source.Path, destination), "不能将源文件用作输出。");
        Require(!File.Exists(destination) && !Directory.Exists(destination) || overwrite && File.Exists(destination), "输出文件已存在，请更换文件名。");
        ValidateOptions(source, options, destination);
        var parent = Path.GetDirectoryName(destination)!;
        Require(Directory.Exists(parent), "请选择已有的输出目录。");
        emit("status", JsonValue.Create("正在读取逐帧时间轴…"));
        if (timeline is null || !timeline.Matches(source.Path))
        {
            timeline = await FrameTimeline.LoadAsync(tools, source, cancellation).ConfigureAwait(false);
        }
        timeline.Resolve(new(options.Frame, options.Frame + 1));
        var motion = options.MotionFrames ?? DefaultMotionRange(timeline, options.Frame);
        if (options.Motion)
        {
            var window = timeline.Resolve(motion);
            Require(options.Frame >= window.Start && options.Frame < window.End, "封面帧必须位于动态照片的视频范围内。");
        }
        var directory = Path.Combine(parent, ".lumarecode-photo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            emit("decoder", JsonValue.Create("software"));
            emit("status", JsonValue.Create("正在提取所选帧…"));
            var image = await RenderAsync(tools, source, timeline, options, directory, cancellation).ConfigureAwait(false);
            string temporary = image.Path;
            double motionDuration = 0;
            if (options.Motion)
            {
                emit("status", JsonValue.Create("正在生成动态照片视频…"));
                bool sdr = options.Format == "sdr";
                var videoOptions = RenderOptions(source, options, sdr, options.Purpose) with
                {
                    Codec = sdr ? "avc" : "hevc",
                    Audio = "aac",
                    Container = "mp4",
                    ColorRange = "limited",
                    Sampling = "420",
                    BitDepth = sdr ? "8" : "10"
                };
                string clip = Path.Combine(directory, "motion.mp4");
                var video = await engine.RunAsync(tools, source, videoOptions, clip, emit, cancellation, selection: motion, motionPhoto: true, cachedTimeline: timeline, motionSize: options.MotionSize).ConfigureAwait(false);
                var movieFrames = await FrameTimeline.LoadAsync(tools, video, cancellation).ConfigureAwait(false);
                int cover = options.Frame - motion.Start;
                Require(cover < movieFrames.Count, "动态照片视频中缺少所选封面帧。");
                long timestampUs = checked((long)Math.Round(movieFrames.Seconds(cover) * 1000000));
                temporary = Path.Combine(directory, "motion_MP.jpg");
                await MotionPhoto.WriteAsync(await File.ReadAllBytesAsync(image.Path, cancellation).ConfigureAwait(false), clip, temporary, timestampUs, cancellation).ConfigureAwait(false);
                MotionPhoto.Inspect(temporary);
                motionDuration = movieFrames.Duration;
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            return new(destination, new FileInfo(destination).Length, image.Width, image.Height,
                options.Format switch
                {
                    "hdr" => "AVIF · HDR PQ",
                    "ultrahdr" => "JPEG · Ultra HDR",
                    "png" => "PNG · SDR",
                    _ => "JPEG · SDR"
                }, motionDuration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<(string Path, int Width, int Height)> RenderAsync(MediaTools tools, MediaInfo source, FrameTimeline timeline,
        ImageExportOptions options, string directory, CancellationToken cancellation)
    {
        bool sdr = options.Format is "sdr" or "png";
        var render = RenderOptions(source, options, sdr);
        var window = timeline.Resolve(new(options.Frame, options.Frame + 1));
        var plan = engine.Plan(tools, source, render, Path.Combine(directory, "unused.mkv"), window: window);
        if (source.Raw is not null)
        {
            await RawDevelopment.PrepareAsync(tools, source, Path.Combine(directory, "raw-calibration.cmd"), null, cancellation, window).ConfigureAwait(false);
        }
        int input = Array.IndexOf(plan.Arguments, "-i");
        var arguments = plan.Arguments.Take(input + 2).ToList();
        string filter = plan.Arguments[Array.IndexOf(plan.Arguments, "-vf") + 1];
        string destination;
        var encoding = new List<string>();
        if (sdr)
        {
            filter += ",zscale=primaries=bt709:transfer=iec61966-2-1:matrix=gbr:range=full,format=gbrp16le,format=" + (options.Format == "png" && OutputPolicy.SourceDepth(source) > 8 ? "rgb48be" : "rgb24")
                + ",iccgen=color_primaries=bt709:color_trc=iec61966-2-1:force=1";
            destination = Path.Combine(directory, options.Format == "png" ? "frame.png" : "frame.jpg");
            encoding.AddRange(options.Format == "png" ? ["-c:v", "png"] : ["-c:v", "mjpeg", "-q:v", Math.Clamp((101 - options.Quality) / 3 + 1, 1, 31).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuvj444p"]);
        }
        else if (options.Format == "hdr")
        {
            int depth = Math.Min(12, Math.Max(10, OutputPolicy.SourceDepth(source)));
            filter += ",libplacebo=format=yuv444p16le:colorspace=bt2020nc:color_primaries=bt2020:color_trc=smpte2084:range=full:apply_dolbyvision=0,format=yuv444p" + depth + "le";
            destination = Path.Combine(directory, "frame.avif");
            encoding.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "6", "-crf", Math.Clamp((100 - options.Quality) / 2 + 10, 0, 63).ToString(CultureInfo.InvariantCulture), "-threads", "2",
                "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "pc", "-f", "avif"]);
        }
        else
        {
            filter += ",zscale=primaries=bt2020:matrix=gbr:range=full,format=gbrp10le,format=x2bgr10le";
            destination = Path.Combine(directory, "frame.rgb10");
            encoding.AddRange(["-c:v", "rawvideo", "-pix_fmt", "x2bgr10le", "-f", "rawvideo"]);
        }
        arguments.AddRange(["-map", "0:" + source.Video.Text("index"), "-an", "-sn", "-dn", "-map_metadata", "-1", "-vf", filter, "-frames:v", "1", "-fps_mode", "passthrough"]);
        arguments.AddRange(encoding);
        arguments.Add(destination);
        var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, arguments.Skip(1), cancellation, 3600,
            source.Raw is not null ? directory : engine.Assets).ConfigureAwait(false);
        Require(result.ExitCode == 0 && File.Exists(destination), "图片提取失败：" + result.Error.Trim());
        if (!sdr && options.Format == "ultrahdr")
        {
            int width = source.Video.Integer("width"), height = source.Video.Integer("height");
            if (source.Raw is { } raw)
            {
                var crop = raw["crop"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
                width = raw.Integer("width") - crop[0] - crop[1];
                height = raw.Integer("height") - crop[2] - crop[3];
                if (RawDevelopment.Orientation(source).Any(f => f.StartsWith("transpose", StringComparison.Ordinal)))
                {
                    (width, height) = (height, width);
                }
            }
            else
            {
                var matrix = source.SideData.FirstOrDefault(s => s.Text("side_data_type") == "Display Matrix");
                if (Math.Abs(Math.Abs(JsonFields.Number(matrix.Text("rotation"))) % 180 - 90) < 1)
                {
                    (width, height) = (height, width);
                }
            }
            byte[] jpeg = UltraHdrCodec.EncodeFile(destination, width, height,
                plan.Colors.Text("color_transfer") == "arib-std-b67", options.Quality, cancellation);
            var info = UltraHdrCodec.Inspect(jpeg);
            destination = Path.Combine(directory, "frame.jpg");
            await File.WriteAllBytesAsync(destination, jpeg, cancellation).ConfigureAwait(false);
            return (destination, info.Width, info.Height);
        }
        var image = await MediaProbe.InspectAsync(tools, destination, cancellation).ConfigureAwait(false);
        if (options.Format == "hdr")
        {
            Require(image.Video.Text("color_transfer") == "smpte2084" && image.Video.Text("color_primaries") == "bt2020" && OutputPolicy.SourceDepth(image) >= 10,
                "HDR 图片色彩或位深校验失败。");
        }
        return (destination, image.Video.Integer("width"), image.Video.Integer("height"));
    }
}
