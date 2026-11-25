using System.Text;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;
using MediaProbe = LumaRecode.Tests.Reference.ReferenceMediaProbe;

internal static class ProbeChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string library)
    {
        Normalize(check);
        string helper = Path.Combine(Path.GetDirectoryName(library)!, OperatingSystem.IsWindows() ? "luma_process_helper.exe" : "luma_process_helper");
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "LumaRecode-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool safeCleanup = true;
        try
        {
            string Fixture(string mode)
            {
                string file = Path.Combine(root, "中文 日本語-" + mode + ".dat");
                File.WriteAllText(file, mode);
                return file;
            }
            foreach (string mode in new[] { "fallback", "explicit", "merge", "dji", "dovi", "dovi_video", "underflow", "overflow", "double_sign" })
            {
                string file = Fixture(mode);
                var current = await NativeProbe.RunAsync(new(helper, file));
                safeCleanup &= current.Snapshot.CleanupConfirmed;
                check(current.Status == NativeStatus.Ok && current.Snapshot.CleanupConfirmed,
                    $"Native fixture probe failed ({mode}): {current.Diagnostic}");
                if (current.Status != NativeStatus.Ok)
                {
                    continue;
                }

                var old = await MediaProbe.InspectAsync(new(helper, helper), file);
                Compare(check, current, old);
                check(current.Snapshot.VideoIndex == 1 && current.Snapshot.AudioCount == 2,
                    "Probe selected cover art or lost an audio track.");
                if (mode == "dovi")
                {
                    check(current.Snapshot.Flags.HasFlag(NativeProbeFlags.DolbyVision | NativeProbeFlags.Hdr10Plus),
                        "Probe lost first-frame dynamic HDR side data.");
                    var side = JsonNode.Parse(current.Data)!["frames"]![0]!["side_data_list"]![1]!;
                    check(side["maxscl"]!.AsArray().Count == 3 && side["nested"]![0] is JsonArray,
                        "Repeated HDR fields or array-valued duplicates changed shape.");
                }
            }
            var raw = await NativeProbe.RunAsync(new(helper, Fixture("raw")));
            safeCleanup &= raw.Snapshot.CleanupConfirmed;
            check(raw.Status == NativeStatus.Ok && raw.Snapshot.VideoIndex == 4
                && raw.Snapshot.Flags.HasFlag(NativeProbeFlags.Raw | NativeProbeFlags.RawCalibrationPending)
                && JsonNode.Parse(raw.Data)!.Text("kind") == "raw", "RAW selection or development handoff differs.");

            foreach (var (mode, status) in new[] { ("no_video", NativeStatus.InvalidData), ("invalid_json", NativeStatus.InvalidData),
                ("deep_json", NativeStatus.ResourceLimit), ("stream_failure", NativeStatus.IoError), ("frame_failure", NativeStatus.IoError) })
            {
                var current = await NativeProbe.RunAsync(new(helper, Fixture(mode)));
                safeCleanup &= current.Snapshot.CleanupConfirmed;
                check(current.Status == status && current.Data.Length == 0 && current.Snapshot.CleanupConfirmed,
                    $"Probe failure contract differs ({mode}): {current.Status}/{current.Diagnostic}");
            }
            string changingSource = Fixture("source_changed");
            var changed = await NativeProbe.RunAsync(new(helper, changingSource));
            safeCleanup &= changed.Snapshot.CleanupConfirmed;
            check(OperatingSystem.IsWindows()
                ? changed.Status == NativeStatus.Ok && File.ReadAllText(changingSource) == "source_changed"
                : changed.Status == NativeStatus.SourceChanged && changed.Data.Length == 0,
                "Probe allowed a source write or ignored an identity/revision change.");
            var budget = await NativeProbe.RunAsync(new(helper, Fixture("fallback"), MetadataCapacity: 2048));
            safeCleanup &= budget.Snapshot.CleanupConfirmed;
            check(budget.Status == NativeStatus.ResourceLimit && budget.Snapshot.Phase == NativeProbePhase.PixelFormats
                && budget.Data.Length == 0, "Cached pixel descriptors bypassed the current capture budget.");
            var capture = await NativeProbe.RunAsync(new(helper, Fixture("large_json"), MetadataCapacity: 512));
            safeCleanup &= capture.Snapshot.CleanupConfirmed;
            check(capture.Status == NativeStatus.ResourceLimit && capture.Data.Length == 0, "Probe capture budget was ignored.");
            using (var before = new CancellationTokenSource())
            {
                before.Cancel();
                var cancelled = await NativeProbe.RunAsync(new(helper, Fixture("wait")), before.Token);
                safeCleanup &= cancelled.Snapshot.CleanupConfirmed;
                check(cancelled.Status == NativeStatus.Cancelled && cancelled.Data.Length == 0, "Pre-cancelled probe ran a child.");
            }
            using (var cancellation = new CancellationTokenSource(300))
            {
                var cancelled = await NativeProbe.RunAsync(new(helper, Fixture("frame_wait")), cancellation.Token);
                safeCleanup &= cancelled.Snapshot.CleanupConfirmed;
                check(cancelled.Status == NativeStatus.Cancelled && cancelled.Snapshot.CleanupConfirmed
                    && cancelled.Data.Length == 0, "Probe cancellation abandoned a subprocess or exposed partial data.");
            }
            var timed = await NativeProbe.RunAsync(new(helper, Fixture("wait"), TimeoutMilliseconds: 100));
            safeCleanup &= timed.Snapshot.CleanupConfirmed;
            check(timed.Status == NativeStatus.TimedOut && timed.Snapshot.CleanupConfirmed, "Probe deadline contract differs.");
            var optional = await NativeProbe.RunAsync(new(helper, Fixture("dji_wait"), TimeoutMilliseconds: 1500));
            safeCleanup &= optional.Snapshot.CleanupConfirmed;
            check(optional.Status == NativeStatus.Ok && optional.Snapshot.CleanupConfirmed
                && JsonNode.Parse(optional.Data)!["dji_status"]!.GetValue<int>() == (int)NativeStatus.TimedOut,
                "Optional DJI timeout did not preserve valid stream inspection.");
            var simultaneous = await Task.WhenAll(new[] { "explicit", "dji", "merge", "fallback" }
                .Select(mode => NativeProbe.RunAsync(new(helper, Fixture(mode)))));
            safeCleanup &= simultaneous.All(item => item.Snapshot.CleanupConfirmed);
            check(simultaneous.All(item => item.Status == NativeStatus.Ok && item.Snapshot.Phase == NativeProbePhase.Complete),
                "Concurrent source inspections interfered with each other.");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(parent))
            {
                throw new InvalidOperationException("Unexpected probe fixture root.");
            }

            if (safeCleanup)
            {
                Directory.Delete(root, recursive: true);
            }
            else
            {
                Console.WriteLine("Retained probe fixtures after unconfirmed child cleanup: " + root);
            }
        }
    }

    internal static void Compare(Action<bool, string> check, NativeProbeResult current, MediaInfo previous)
    {
        var data = JsonNode.Parse(current.Data)!;
        check(data.Text("kind") == previous.Kind && data["profile"]?.ToString() == previous.Profile
            && Math.Abs(current.Snapshot.Duration - previous.Duration) < 1e-9, "Native inspection classification/duration differs.");
        foreach (var pair in new[] { ("video", (JsonNode)previous.Video), ("streams", previous.Streams),
            ("frames", previous.Frames), ("tags", previous.Tags), ("pixel", previous.Pixel) })
        {
            check(JsonNode.DeepEquals(data[pair.Item1], pair.Item2), "Native inspection snapshot differs: " + pair.Item1);
        }
    }

    private static void Normalize(Action<bool, string> check)
    {
        foreach (var (input, expected) in new[] {
            ("{\"a\":1,\"b\":2,\"a\":3,\"c\":4,\"b\":5}", "{\"a\":[1,3],\"c\":4,\"b\":[2,5]}"),
            ("{\"x\":[1],\"x\":[2],\"x\":null}", "{\"x\":[[1],[2],null]}"),
            ("{\"x\":{\"y\":1,\"y\":2},\"u\":\"中文 日本語 🌈\"}", "{\"x\":{\"y\":[1,2]},\"u\":\"中文 日本語 🌈\"}"),
            ("{\"n\":18446744073709551615}", "{\"n\":18446744073709551615}"),
            ("[true,false,null,1.5]", "[true,false,null,1.5]") })
        {
            var normalized = NativeProbe.NormalizeJson(Encoding.UTF8.GetBytes(input));
            check(JsonNode.DeepEquals(JsonNode.Parse(normalized), JsonNode.Parse(expected)), "Probe JSON normalization differs.");
            if (input.StartsWith("{\"a\"", StringComparison.Ordinal))
            {
                check(JsonNode.Parse(normalized)!.AsObject().Select(pair => pair.Key).SequenceEqual(new[] { "a", "c", "b" }),
                    "Duplicate normalization changed tag precedence.");
            }
        }
        foreach (byte[] input in new byte[][] { [], [255], Encoding.UTF8.GetBytes("{\"x\":1,}"),
            Encoding.UTF8.GetBytes(new string('[', 70) + "0" + new string(']', 70)) })
        {
            bool rejected = false;
            try
            {
                NativeProbe.NormalizeJson(input);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Invalid or over-depth JSON was accepted.");
        }
        string many = "{" + string.Join(',', Enumerable.Range(0, 10000).Select(index => $"\"k{index}\":{index}")) + "}";
        check(JsonNode.Parse(NativeProbe.NormalizeJson(Encoding.UTF8.GetBytes(many)))!.AsObject().Count == 10000,
            "Large probe objects lost fields.");
    }
}
