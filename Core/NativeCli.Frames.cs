using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

public sealed partial class NativeCli
{
    private static bool HasFrameSelection(CliArguments args) => new[] { "start_frame", "end_frame", "start_time", "end_time" }.Any(key => args.Get(key) is not null);

    private static int FrameInteger(CliArguments args, string key, int fallback)
    {
        if (args.Get(key) is not { } value)
        {
            return fallback;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int frame) || frame < 0)
        {
            throw new CliException("invalid_arguments", "--" + key.Replace('_', '-') + " requires a non-negative integer.");
        }
        return frame;
    }

    private static FrameRange ReadFrameSelection(CliArguments args, FrameTimeline timeline)
    {
        if (args.Duration is not null)
        {
            throw new CliException("invalid_arguments", "逐帧裁剪不能同时使用 --duration。");
        }
        bool times = args.Get("start_time") is not null || args.Get("end_time") is not null;
        if (times && (args.Get("start_frame") is not null || args.Get("end_frame") is not null))
        {
            throw new CliException("invalid_arguments", "Frame and time ranges are mutually exclusive.");
        }
        int TimeFrame(string key, int fallback)
        {
            if (args.Get(key) is not { } value)
            {
                return fallback;
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds > timeline.Duration)
            {
                throw new CliException("invalid_arguments", "Invalid --" + key.Replace('_', '-') + ".");
            }
            return seconds >= timeline.Duration ? timeline.Count : timeline.AtOrAfter(seconds);
        }
        var range = times ? new FrameRange(TimeFrame("start_time", 0), TimeFrame("end_time", timeline.Count))
            : new FrameRange(FrameInteger(args, "start_frame", 0), FrameInteger(args, "end_frame", timeline.Count));
        timeline.Resolve(range);
        return range;
    }

    private async Task<int> FrameCommandAsync(CliArguments args, MediaTools tools, CliReporter report, CancellationToken cancellation)
    {
        var media = await MediaProbe.InspectAsync(tools, args.Inputs[0], cancellation);
        var timeline = await FrameTimeline.LoadAsync(tools, media, cancellation);
        if (args.Command == "frames")
        {
            Output(new JsonObject
            {
                ["schema_version"] = 1,
                ["input"] = media.Path,
                ["frames"] = timeline.Count,
                ["duration"] = timeline.Duration,
                ["time_base"] = media.Video.Text("time_base"),
                ["first_pts"] = timeline.Frames[0].Pts,
                ["last_pts"] = timeline.Frames[^1].Pts
            });
            return 0;
        }
        string kind = args.Get("input_kind") ?? "auto";
        if (kind == "auto")
        {
            kind = media.Kind;
        }
        string? profile = args.Get("profile") is null or "auto" ? media.Profile : args.Get("profile");
        if (kind == "unknown" && profile is not null)
        {
            kind = "log";
        }
        if (kind == "unknown")
        {
            throw new CliException("ambiguous_input", T("无法确认传递曲线，请指定 --input-kind。"), 4);
        }
        string format = args.Get("image_format") ?? "auto";
        if (format == "auto")
        {
            format = kind is "hlg" or "pq" or "log" or "raw" ? "ultrahdr" : "sdr";
        }
        if (format is not ("hdr" or "ultrahdr" or "sdr" or "png"))
        {
            throw new CliException("invalid_arguments", "Invalid --image-format.");
        }
        int frame = FrameInteger(args, "frame", 0);
        timeline.Resolve(new(frame, frame + 1));
        int quality = FrameInteger(args, "quality", 95);
        if (quality is < 1 or > 100)
        {
            throw new CliException("invalid_arguments", T("图片质量须在 1 到 100 之间。"));
        }
        var motion = FrameExporter.DefaultMotionRange(timeline, frame);
        motion = new(FrameInteger(args, "motion_start_frame", motion.Start), FrameInteger(args, "motion_end_frame", motion.End ?? timeline.Count));
        bool includeMotion = args.Has("motion");
        if (!includeMotion && new[] { "motion_start_frame", "motion_end_frame", "motion_size", "motion_range" }.Any(key => args.Get(key) is not null))
        {
            throw new CliException("invalid_arguments", "Motion video options require --motion.");
        }
        var options = new ImageExportOptions(frame, format, kind, profile, includeMotion, motion, quality,
            args.Get("purpose") ?? "archive", args.Get("motion_size") ?? "source", args.Get("motion_range") ?? "sdr");
        if (includeMotion)
        {
            timeline.Resolve(motion);
            if (frame < motion.Start || frame >= motion.End)
            {
                throw new CliException("invalid_arguments", T("封面帧必须位于动态照片的视频范围内。"));
            }
        }
        string destination = Path.GetFullPath(args.Get("output") ?? Path.Combine(Path.GetDirectoryName(media.Path)!,
            $"{Path.GetFileNameWithoutExtension(media.Path)}_{frame:000000}_{format}{(includeMotion ? "_MP" : "")}{options.Suffix}"));
        if (ProcessRunner.SameFile(media.Path, destination) || Directory.Exists(destination))
        {
            throw new CliException("output_conflict", T($"输出路径冲突：{destination}"), 5);
        }
        FrameExporter.ValidateOptions(media, options, destination);
        if (args.Get("log") is { } log)
        {
            if (ProcessRunner.SameFile(log, media.Path) || ProcessRunner.SameFile(log, destination))
            {
                throw new CliException("output_conflict", T("日志路径与输入或输出冲突。"), 5);
            }
            report.OpenLog(log);
        }
        report.Context = new JsonObject { ["input"] = media.Path, ["output"] = destination };
        if (args.Has("dry_run"))
        {
            report.Emit("plan", new JsonObject
            {
                ["frame"] = frame,
                ["time"] = timeline.Position(frame),
                ["format"] = format,
                ["motion"] = includeMotion,
                ["motion_start_frame"] = motion.Start,
                ["motion_end_frame"] = motion.End,
                ["motion_size"] = options.MotionSize,
                ["motion_range"] = options.MotionVideoRange,
                ["purpose"] = options.Purpose
            });
            return 0;
        }
        if (File.Exists(destination) && args.Has("skip_existing"))
        {
            report.Emit("skipped", JsonValue.Create(destination));
            return 0;
        }
        bool overwrite = args.Has("overwrite");
        if (File.Exists(destination) && !overwrite)
        {
            if (!await ConfirmOverwriteAsync(destination, cancellation))
            {
                report.Emit("cancelled", JsonValue.Create(destination));
                return 130;
            }
            overwrite = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        report.Clock.Restart();
        report.Emit("start", new JsonObject { ["frame"] = frame, ["format"] = format, ["motion"] = includeMotion });
        var result = await new FrameExporter(engine).ExportAsync(tools, media, options, destination, report.Emit, overwrite, cancellation, timeline);
        report.Emit("done", new JsonObject
        {
            ["path"] = result.Path,
            ["bytes"] = result.Bytes,
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["format"] = result.Format,
            ["motion_duration"] = result.MotionDuration
        });
        return 0;
    }
}
