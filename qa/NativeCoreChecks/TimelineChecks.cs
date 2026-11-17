using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;

internal static class TimelineChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(Unsafe.SizeOf<NativeFramePoint>() == 24, "Managed frame point ABI differs.");
        var random = new Random(617);
        foreach (bool packets in new[] { false, true })
            foreach (int count in new[] { 1, 2, 17, 129 })
                foreach (long numerator in new long[] { 1, 1001 })
                    foreach (long denominator in new long[] { 1000, 90000 })
                    {
                        long pts = -5000;
                        var points = Enumerable.Range(0, count).Select(index =>
                        {
                            pts += random.Next(1, 4000);
                            return new FramePoint(pts, index % 3 == 0 ? 0 : random.Next(1, 900), index % 7 == 0);
                        }).ToArray();
                        using var native = NativeTimeline.FromPoints(points.Select(p => new NativeFramePoint(p.Pts, p.Duration,
                            p.KeyFrame ? 1U : 0)).ToArray(), numerator, denominator, packets);
                        var baseline = new FrameTimeline(points, numerator, denominator, packets);
                        Compare(check, native, baseline);
                    }
    }

    private static void Compare(Action<bool, string> check, NativeTimeline native, FrameTimeline baseline)
    {
        var snapshot = native.Snapshot;
        check(snapshot.Count == (ulong)baseline.Count && snapshot.PacketIndexed == baseline.PacketIndexed
            && snapshot.TimeBaseNumerator == baseline.TimeBaseNumerator
            && snapshot.TimeBaseDenominator == baseline.TimeBaseDenominator
            && Math.Abs(snapshot.Duration - baseline.Duration) <= 1e-9 * Math.Max(1, baseline.Duration),
            "Native timeline summary differs from managed baseline.");
        var copied = native.CopyPoints(0, baseline.Count);
        for (int i = 0; i < baseline.Count; i++)
        {
            var point = baseline.Frames[i];
            check(copied[i] == new NativeFramePoint(point.Pts, point.Duration, point.KeyFrame ? 1U : 0),
                "Native timeline display point differs.");
            foreach (double delta in new[] { -.00001, 0, .00001 })
            {
                double position = baseline.Position(i) + delta;
                check(native.AtOrAfter(position) == (ulong)baseline.AtOrAfter(position),
                    "Native timeline binary time lookup differs.");
            }
            var end = Math.Min(i + 3, baseline.Count);
            var expected = baseline.Resolve(new(i, end));
            var actual = native.Resolve((ulong)i, (ulong)end);
            check(actual.Start == (ulong)expected.Start && actual.End == (ulong)expected.End
                && actual.SeekFrame == (ulong)expected.SeekFrame && actual.SeekSeconds == expected.SeekSeconds
                && actual.StartSeconds == expected.StartSeconds && actual.EndSeconds == expected.EndSeconds,
                "Native timeline selected range differs.");
        }
        check(native.AtOrAfter(-1000000) == 0 && native.AtOrAfter(1e20) == (ulong)baseline.Count,
            "Native timeline endpoint lookup differs.");
    }

    internal static async Task<bool> SoftwareSamplesAsync(Action<bool, string> check,
        string ffmpeg, string ffprobe, string root)
    {
        bool cleanupConfirmed = true;
        var tools = new MediaTools(ffmpeg, ffprobe);
        foreach (string codec in new[] { "libx264", "ffv1" })
        {
            string source = Path.Combine(root, "timeline-" + codec + ".mkv");
            var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i",
                "testsrc2=size=128x72:rate=40:duration=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-vf", "select=not(eq(mod(n\\,5)\\,1)),setpts=PTS+0.5/TB,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=limited", "-fps_mode", "passthrough",
                "-c:v", codec, "-threads", "1", "-c:a", "pcm_s16le", "-af", "asetpts=PTS+0.5/TB" };
            if (codec == "libx264")
                arguments.AddRange(["-preset", "ultrafast", "-g", "5", "-bf", "2"]);
            arguments.AddRange(["-n", source]);
            var encoded = await NativeProcess.RunAsync(new(ffmpeg, arguments));
            cleanupConfirmed &= encoded.CleanupConfirmed;
            check(encoded.Status == NativeStatus.Ok, "Timeline VFR fixture encode failed: " + encoded.ErrorText);
            if (encoded.Status != NativeStatus.Ok)
                continue;
            var media = await MediaProbe.InspectAsync(tools, source);
            var timeBase = media.Video.Text("time_base").Split('/');
            var options = new NativeTimelineOptions(ffprobe, source, media.Video.Text("codec_name"),
                media.Video.Integer("index"), long.Parse(timeBase[0]), long.Parse(timeBase[1]));
            var loaded = await NativeTimeline.LoadAsync(options);
            cleanupConfirmed &= loaded.Snapshot.CleanupConfirmed;
            check(loaded.Status == NativeStatus.Ok, "Native VFR timeline scan failed: " + loaded.Diagnostic);
            using var timeline = loaded.Timeline;
            if (timeline is null)
                continue;
            var baseline = await FrameTimeline.LoadAsync(tools, media);
            Compare(check, timeline, baseline);
            check(timeline.Snapshot.Count == 32 && timeline.Snapshot.PacketIndexed == (codec == "libx264"),
                "Real timeline packet/decoded route or VFR frame count differs.");
            var points = timeline.CopyPoints(0, 32);
            check(points.Zip(points.Skip(1), (a, b) => b.Pts - a.Pts).Distinct().Count() > 1,
                "VFR fixture lost its nonuniform display cadence.");
            var output = Path.Combine(root, "timeline-selected-" + codec + ".mkv");
            using var transaction = NativeOutputTransaction.Create(source, output);
            bool jobCleaned = true;
            try
            {
                var selected = timeline.Resolve(7, 23);
                var window = new FrameWindow((int)selected.Start, (int)selected.End, (int)selected.SeekFrame,
                    selected.SeekSeconds, selected.StartSeconds, selected.EndSeconds);
                var planner = new Transcoder(Path.Combine(Directory.GetCurrentDirectory(), "Assets"));
                var sourceProbe = await NativeProbe.RunAsync(new(ffprobe, source));
                jobCleaned &= sourceProbe.Snapshot.CleanupConfirmed;
                check(sourceProbe.Status == NativeStatus.Ok, "Native selected-window source probe failed.");
                if (sourceProbe.Status != NativeStatus.Ok)
                    continue;
                var request = PlanChecks.Request(planner, tools, media, new(Purpose: "archive", Codec: "avc", Audio: "aac"),
                    transaction.StagingPath, window: window) with
                {
                    MediaJson = sourceProbe.Data
                };
                var planned = NativePlanner.Create(request);
                check(planned.Status == NativeStatus.Ok, "Native selected-window plan failed: " + planned.Diagnostic);
                if (planned.Status != NativeStatus.Ok)
                    continue;
                var plan = JsonNode.Parse(planned.Data)!.AsObject();
                var args = plan["arguments"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
                var converted = await NativeProcess.RunAsync(new(args[0], args[1..],
                    WorkingDirectory: plan["working_directory"]?.GetValue<string>() ?? planner.Assets));
                jobCleaned = converted.CleanupConfirmed;
                check(converted.Status == NativeStatus.Ok, "Native selected-window encode failed: " + converted.ErrorText);
                if (converted.Status != NativeStatus.Ok)
                    continue;
                var outputProbe = await NativeProbe.RunAsync(new(ffprobe, transaction.StagingPath));
                jobCleaned &= outputProbe.Snapshot.CleanupConfirmed;
                check(outputProbe.Status == NativeStatus.Ok, "Native selected-window output probe failed.");
                if (outputProbe.Status != NativeStatus.Ok)
                    continue;
                var verified = NativePlanner.VerifyOutput(request, outputProbe.Data);
                check(verified.Status == NativeStatus.Ok, "Native selected-window output verification failed: " + verified.Diagnostic);
                if (verified.Status != NativeStatus.Ok)
                    continue;
                var inspected = JsonNode.Parse(outputProbe.Data)!["video"]!;
                var result = await NativeTimeline.LoadAsync(new(ffprobe, transaction.StagingPath,
                    inspected.Text("codec_name"), inspected.Integer("index"),
                    1, long.Parse(inspected.Text("time_base").Split('/')[1])));
                jobCleaned &= result.Snapshot.CleanupConfirmed;
                using var outputTimeline = result.Timeline;
                check(result.Status == NativeStatus.Ok && result.Snapshot.Count == 16,
                    "Selected native frame window did not produce exactly 16 display frames.");
                check(transaction.Seal(jobCleaned, out _) == NativeStatus.Ok
                    && transaction.Commit(out _) == NativeStatus.Ok && File.Exists(output),
                    "Selected-window native output transaction failed.");
            }
            finally
            {
                var cleaned = transaction.Cleanup(jobCleaned, out _);
                cleanupConfirmed &= jobCleaned && cleaned == NativeStatus.Ok;
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = await NativeTimeline.LoadAsync(options, cancelled.Token);
            cleanupConfirmed &= stopped.Snapshot.CleanupConfirmed;
            check(stopped.Status == NativeStatus.Cancelled && stopped.Timeline is null,
                "Pre-cancelled managed timeline did not return a cleaned terminal status.");
        }
        Console.WriteLine("VFR/B-frame display indexes, time selection and transactional trimmed encodes passed.");
        return cleanupConfirmed;
    }
}
