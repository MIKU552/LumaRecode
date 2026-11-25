using System.Diagnostics;
using System.Text;
using LumaRecode.Native;
using VideoConverter.Core;
using Reference = LumaRecode.Tests.Reference;

internal static class HostIntegrationChecks
{
    internal static async Task StreamingAsync(Action<bool, string> check, string library)
    {
        var lines = new List<string>();
        var decoder = new Utf8LineConsumer(lines.Add);
        foreach (byte value in Encoding.UTF8.GetBytes("帧 😀\r\nprogress=continue\n尾行"))
        {
            decoder.Feed([value]);
        }
        decoder.Feed([], flush: true);
        check(lines.SequenceEqual(new[] { "帧 😀", "progress=continue", "尾行" }),
            "UTF-8 progress decoder split a character, CRLF or final line.");
        bool bounded = false;
        var oversized = new Utf8LineConsumer(_ => { });
        oversized.Feed(new byte[MediaResourceLimits.LineCharacters]);
        try
        {
            oversized.Feed([1]);
        }
        catch (InvalidDataException) { bounded = true; }
        check(bounded, "Progress decoder did not enforce its incomplete-line budget.");
        string helper = Path.Combine(Path.GetDirectoryName(library)!, OperatingSystem.IsWindows() ? "luma_process_helper.exe" : "luma_process_helper");
        ulong output = 0, errors = 0;
        var result = await NativeProcess.RunStreamingAsync(new(helper, ["binary", "350000"],
            OutputMode: NativeCaptureMode.Discard), (stream, bytes) =>
        {
            if (stream == 1)
            {
                for (int i = 0; i < bytes.Length; ++i)
                {
                    if (bytes[i] != (byte)((output + (ulong)i) % 251))
                    {
                        throw new InvalidDataException("Streamed binary order differs.");
                    }
                }

                output += (ulong)bytes.Length;
            }
            else
            {
                errors += (ulong)bytes.Length;
            }
        });
        check(result.Status == NativeStatus.Ok && result.CleanupConfirmed && output == 350000 && errors > 150000
            && result.Output.Length == 0 && result.OutputBytesSeen == output,
            "Native streaming lost bytes or retained complete stdout.");
        var timer = Stopwatch.StartNew();
        bool observed = false;
        try
        {
            await NativeProcess.RunStreamingAsync(new(helper, ["binary", "350000"], OutputMode: NativeCaptureMode.Discard),
                (_, _) => throw new InvalidDataException("consumer-check"));
        }
        catch (InvalidDataException error) when (error.Message == "consumer-check") { observed = true; }
        check(observed && timer.Elapsed < TimeSpan.FromSeconds(5), "Output consumer failure was not joined/rethrown promptly.");
    }

    internal static async Task<bool> VideoAsync(Action<bool, string> check, string ffmpeg, string ffprobe,
        string source, string destination, bool hdr)
    {
        var tools = new MediaTools(ffmpeg, ffprobe);
        var media = await MediaProbe.InspectAsync(tools, source);
        var reference = await Reference.ReferenceMediaProbe.InspectAsync(tools, source);
        check(media.Kind == reference.Kind && media.Profile == reference.Profile && media.Evidence == reference.Evidence,
            "Production inspection bridge changed source classification/evidence.");
        var timeline = await FrameTimeline.LoadAsync(tools, media);
        var baseline = await Reference.ReferenceFrameTimeline.LoadAsync(tools, reference);
        check(timeline.Count == baseline.Count && timeline.Resolve(new()) == baseline.Resolve(new())
            && timeline.Frames.SequenceEqual(baseline.Frames), "Production timeline view differs.");
        foreach (string purpose in new[] { "share", "archive" })
        {
            foreach (string codec in new[] { "avc", "hevc", "av1" })
            {
                check(OutputPolicy.DepthChoices(media, purpose, codec, hdr ? "hdr" : "sdr").SequenceEqual(
                    Reference.ReferenceOutputPolicy.DepthChoices(reference, purpose, codec, hdr ? "hdr" : "sdr")), "UI depth choices differ.");
                check(OutputPolicy.DefaultSampling(media, purpose, codec) == Reference.ReferenceOutputPolicy.DefaultSampling(reference, purpose, codec),
                    "UI sampling defaults differ.");
            }
        }

        var engine = new Transcoder(Path.Combine(Directory.GetCurrentDirectory(), "Assets"));
        bool progress = false, decoder = false;
        var encoded = await engine.RunAsync(tools, media, new(Purpose: "archive", Output: hdr ? "hdr" : "sdr"), destination,
            (type, _) => { progress |= type == "progress"; decoder |= type == "decoder"; });
        check(File.Exists(destination) && encoded.Path == destination && encoded.Kind == media.Kind && progress && decoder,
            "Production transcode did not complete native progress/verification/publication.");
        if (!hdr)
        {
            var filters = await NativeProcess.RunAsync(new(ffmpeg, ["-hide_banner", "-filters"]));
            if (!Encoding.UTF8.GetString(filters.Output).Contains("iccgen", StringComparison.Ordinal))
            {
                Console.WriteLine("Skipped image execution: pinned test FFmpeg does not provide iccgen.");
                return true;
            }
            var preview = await new FramePreview(engine).RenderAsync(tools, media, timeline, new(Format: "png"));
            check(preview.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Production viewport preview failed.");
            string image = Path.ChangeExtension(destination, ".png");
            var photo = await new FrameExporter(engine).ExportAsync(tools, media, new(Format: "png"), image, (_, _) => { });
            check(photo.Width > 0 && photo.Height > 0 && File.Exists(image), "Production still-image export failed.");
        }
        return true;
    }
}
