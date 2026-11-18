using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

public sealed partial class Transcoder
{
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
