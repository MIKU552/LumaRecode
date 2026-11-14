using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

public sealed partial class Transcoder
{
    internal static string MotionScaleFilter(string size)
    {
        Require(size is "1080" or "720", "请选择有效的动态视频分辨率。");
        int longSide = size == "1080" ? 1920 : 1280, shortSide = size == "1080" ? 1080 : 720;
        return $"scale=w='min(iw,if(gte(iw,ih),{longSide},{shortSide}))':h='min(ih,if(gte(iw,ih),{shortSide},{longSide}))':force_original_aspect_ratio=decrease:force_divisible_by=2";
    }

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
                    Audio = options.Container != "mp4" && OutputPolicy.FlacEligibility(media).Eligible ? "flac" : "aac"
                };
            }
            Require(ResolveAudio(options) != "copy", "逐帧裁剪需要重新编码音轨，请选择 AAC、Opus 或 FLAC。");
        }
        return options;
    }

    public async Task<EncodingPlan> PlanAsync(MediaTools tools, MediaInfo media, EncodeOptions options, string destination,
        FrameRange? selection = null, double? seconds = null, bool motionPhoto = false, CancellationToken cancellation = default, FrameTimeline? cachedTimeline = null)
    {
        var timeline = selection is null ? null : cachedTimeline?.Matches(media.Path) == true ? cachedTimeline : await FrameTimeline.LoadAsync(tools, media, cancellation).ConfigureAwait(false);
        return Plan(tools, media, options, destination, seconds, selection is null ? null : timeline!.Resolve(selection), motionPhoto);
    }

    /// <summary>Reads the selected first frame's side data for post-trim HDR validation.</summary>
    private static async Task<MediaInfo> FirstSelectedFrameAsync(MediaTools tools, MediaInfo media, FrameTimeline timeline, int frame, CancellationToken cancellation)
    {
        if (frame == 0 || !media.DynamicHdr)
        {
            return media;
        }
        double time = timeline.Seconds(frame);
        string interval = time.ToString("G17", CultureInfo.InvariantCulture) + "%" + (time + 1).ToString("G17", CultureInfo.InvariantCulture);
        var probe = await MediaProbe.ReadAsync(tools, ["-select_streams", media.Video.Text("index"), "-read_intervals", interval,
            "-show_frames", "-of", "json", media.Path], cancellation).ConfigureAwait(false);
        long pts = timeline.Frames[frame].Pts;
        var first = probe["frames"].Objects().FirstOrDefault(f => long.TryParse(f.Text("best_effort_timestamp"), out var value) && value == pts)
            ?? throw new InvalidDataException("无法读取所选开始帧的 HDR 元数据。");
        return media with
        {
            Frames = new JsonArray(first.DeepClone())
        };
    }
}
