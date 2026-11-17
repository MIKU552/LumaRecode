using System.Text;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;

internal static class ProcessChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string library)
    {
        var helper = Path.Combine(Path.GetDirectoryName(library)!, OperatingSystem.IsWindows()
            ? "luma_process_helper.exe" : "luma_process_helper");
        check(File.Exists(helper), "Native process test helper was not built.");
        if (!File.Exists(helper))
        {
            return;
        }
        string[] words = ["", "中文 日本語 🌈", "a\\\"b", "C:\\path\\", "&|<>$()"];
        var mutable = new List<string> { "echo" };
        mutable.AddRange(words);
        var running = NativeProcess.RunAsync(new(helper, mutable));
        mutable[1] = "changed after create";
        var echoed = await running;
        check(echoed.CleanupConfirmed, "Managed success returned before native cleanup.");
        string expected = string.Concat(words.Select(word => $"{Encoding.UTF8.GetByteCount(word)}:{word}\n"));
        check(echoed.Status == NativeStatus.Ok && Encoding.UTF8.GetString(echoed.Output) == expected,
            "Managed arguments were changed, reinterpreted or not copied.");
        foreach (int size in new[] { 0, 8193, 200000 })
        {
            var result = await NativeProcess.RunAsync(new(helper, ["binary", size.ToString()], OutputCapacity: size));
            check(result.Status == NativeStatus.Ok && result.Output.Length == size && result.OutputBytesSeen == (ulong)size,
                "Managed binary capture size differs.");
            check(result.Output.Select((value, index) => value == index % 251).All(value => value),
                "Managed binary output changed.");
            check(result.Error.Length == 8192 && result.ErrorText.EndsWith("diagnostic-tail", StringComparison.Ordinal),
                "Managed diagnostic tail differs.");
        }
        var failure = await NativeProcess.RunAsync(new(helper, ["fail"]));
        check(failure.Status == NativeStatus.IoError && failure.ExitCode == 17 && failure.ErrorText == "failure-reason",
            "Managed nonzero exit differs.");
        var overflow = await NativeProcess.RunAsync(new(helper, ["binary", "32"], OutputCapacity: 31));
        check(overflow.CleanupConfirmed, "Output-limit failure did not confirm native cleanup.");
        check(overflow.Status == NativeStatus.ResourceLimit && overflow.Output.Length == 31,
            "Managed output budget was not enforced.");
        var timeout = await NativeProcess.RunAsync(new(helper, ["wait"], TimeoutMilliseconds: 60));
        check(timeout.CleanupConfirmed, "Timeout did not confirm native cleanup.");
        check(timeout.Status == NativeStatus.TimedOut, "Managed timeout did not wait for native cleanup.");
        using var cancellation = new CancellationTokenSource(100);
        var cancelled = await NativeProcess.RunAsync(new(helper, ["wait"]), cancellation.Token);
        check(cancelled.CleanupConfirmed, "Cancellation did not confirm native cleanup.");
        check(cancelled.Status == NativeStatus.Cancelled, "Managed cancellation did not reach native context.");
        using var early = new CancellationTokenSource();
        early.Cancel();
        var beforeStart = await NativeProcess.RunAsync(new(helper, ["wait"]), early.Token);
        check(beforeStart.Status == NativeStatus.Cancelled && beforeStart.OutputBytesSeen == 0,
            "A pre-cancelled native invocation launched a child.");
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(index => NativeProcess.RunAsync(new(helper, ["echo", index.ToString()]))));
        check(simultaneous.All(result => result.Status == NativeStatus.Ok)
            && simultaneous.Select(result => Encoding.UTF8.GetString(result.Output)).Distinct().Count() == 4,
            "Concurrent native contexts interfered with each other.");

        // Install the runtime's process-exit handling as well. Native children
        // must retain their exit status while managed Process instances coexist.
        using var managedCancellation = new CancellationTokenSource(250);
        var managed = ProcessRunner.CaptureAsync(helper, ["wait"], managedCancellation.Token);
        var alongside = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(index => NativeProcess.RunAsync(new(helper, ["echo", $"coexist-{index}"]))));
        check(alongside.All(result => result.Status == NativeStatus.Ok),
            "Managed process-exit handling interfered with native children.");
        bool managedCancelled = false;
        try
        {
            await managed;
        }
        catch (OperationCanceledException)
        {
            managedCancelled = true;
        }
        check(managedCancelled, "Managed sidecar did not finish cancellation cleanup.");
        foreach (var invalid in new[] { "embedded\0NUL", "invalid\ud800surrogate" })
        {
            bool rejected = false;
            try
            {
                await NativeProcess.RunAsync(new(helper, ["echo", invalid]));
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            check(rejected, "Invalid managed process text reached the executable.");
        }

        var ffmpeg = ProcessRunner.FindTool("ffmpeg", Environment.GetEnvironmentVariable("LUMA_TEST_FFMPEG") ?? "");
        var ffprobe = ProcessRunner.FindTool("ffprobe", Environment.GetEnvironmentVariable("LUMA_TEST_FFPROBE") ?? "");
        if (ffmpeg.Length == 0 || ffprobe.Length == 0)
        {
            if (Environment.GetEnvironmentVariable("LUMA_REQUIRE_MEDIA_TOOLS") == "1")
            {
                check(false, "Required FFmpeg software checks have no executable tools.");
            }
            Console.WriteLine("FFmpeg software smoke: not run (FFmpeg/ffprobe unavailable).");
            return;
        }
        await CheckMediaAsync(check, ffmpeg, ffprobe);
    }

    private static async Task CheckMediaAsync(Action<bool, string> check, string ffmpeg, string ffprobe)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "LumaRecode-native-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "中文 日本語 software.mkv");
            var encoded = await NativeProcess.RunAsync(new(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=128x72:r=10:d=1",
                "-c:v", "ffv1", "-threads", "1", "-progress", "pipe:1", "-n", output], TimeoutMilliseconds: 30000));
            check(encoded.Status == NativeStatus.Ok && File.Exists(output)
                && Encoding.UTF8.GetString(encoded.Output).Contains("progress=end", StringComparison.Ordinal),
                "Native FFmpeg software encode failed: " + encoded.ErrorText + encoded.Diagnostic);
            if (encoded.Status != NativeStatus.Ok)
            {
                return;
            }
            var probed = await NativeProcess.RunAsync(new(ffprobe,
                ["-v", "error", "-count_frames", "-show_entries", "stream=codec_name,width,height,nb_read_frames",
                "-of", "json", output], OutputCapacity: 65536));
            check(probed.Status == NativeStatus.Ok, "Native ffprobe failed: " + probed.Diagnostic);
            if (probed.Status != NativeStatus.Ok)
            {
                return;
            }
            var stream = JsonNode.Parse(probed.Output)!["streams"]![0]!;
            check(stream["codec_name"]!.GetValue<string>() == "ffv1"
                && stream["width"]!.GetValue<int>() == 128 && stream["height"]!.GetValue<int>() == 72
                && stream["nb_read_frames"]!.GetValue<string>() == "10", "Native software media output differs.");
            var frame = await NativeProcess.RunAsync(new(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-i", output, "-frames:v", "1", "-threads", "1",
                "-f", "image2pipe", "-c:v", "png", "pipe:1"]));
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
            check(frame.Status == NativeStatus.Ok && frame.Output.AsSpan().StartsWith(png),
                "Native FFmpeg binary frame extraction failed.");
            Console.WriteLine("FFmpeg software encode, probe and binary frame extraction passed.");
        }
        finally
        {
            // Only remove the generated child of the canonical temporary root.
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(parent))
            {
                throw new InvalidOperationException("Unexpected native media test directory.");
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
