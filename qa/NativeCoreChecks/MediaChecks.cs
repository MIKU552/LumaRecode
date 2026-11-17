using System.Text;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;

internal static class MediaChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Classification(check);
        BinaryMetadata(check);
        HexDumps(check);
    }

    private static KeyValuePair<string, string>[] Tags(params string[] pairs) => Enumerable.Range(0, pairs.Length / 2)
        .Select(index => KeyValuePair.Create(pairs[index * 2], pairs[index * 2 + 1])).ToArray();

    private static void Classification(Action<bool, string> check)
    {
        KeyValuePair<string, string>[][] tags = [[],
            Tags("com.apple.proapps.logcolorspace", "com.apple.log.apple-log"),
            Tags("COM.APPLE.PROAPPS.COLOR.LOGCOLORSPACE", "COM.APPLE.APPLE-WIDE-GAMUT.APPLE-LOG"),
            Tags("make", "Apple", "model", "iPhone 17 Pro"),
            Tags("com.xiaomi.record_log", "YES"), Tags("com.xiaomi.record_log", "0"),
            Tags("com.xiaomi.record_log", "1", "COM.XIAOMI.RECORD_LOG", "0"),
            Tags("com.samsung.android.logvideo", "1"), Tags("com.samsung.android.logvideo", "false"),
            Tags("com.samsung.android.logvideo", "vendor-version"),
            Tags("color_mode", "DJI D-Log M", "model", "Osmo Pocket 3"),
            Tags("gamma", "D-Log M"), Tags("colorgamma", "Mi-Log"),
            Tags("log_profile", "Samsung Log"), Tags("gamma", "S-Log3"),
            Tags("enable_log", "true"), Tags("enable_log", "false"),
            Tags("gamma", "Mi-Log", "GAMMA", "Samsung Log"),
            Tags("其他 🌈", "中文 日本語", "gamma", "standard"),
            Tags("com.apple.proapps.logcolorspace", "com.apple.log.apple-log", "com.xiaomi.record_log", "1"),
            Tags("com.xiaomi.record_log", "1", "com.samsung.android.logvideo", "1")];
        string[] transfers = ["", "unknown", "arib-std-b67", "smpte2084", "bt709", "smpte170m", "bt470m",
            "bt470bg", "gamma22", "gamma28", "iec61966-2-1", "bt2020-10", "bt2020-12", "log100"];
        DjiColor?[] dji = [null, new(DjiSchema.Pocket3, 19), new(DjiSchema.Ac206, 19), new(DjiSchema.Oq101, 9),
            new(DjiSchema.Pocket3, 0), new(DjiSchema.Pocket3, 1), new(DjiSchema.Pocket3, 2), new(DjiSchema.Pocket3, 22),
            new(DjiSchema.Unknown, 19), new(DjiSchema.Pocket3, 999)];
        foreach (var entries in tags)
            foreach (var transfer in transfers)
                foreach (var codec in new[] { "hevc", "h264", "prores_raw" })
                    foreach (var color in dji)
                    {
                        var video = new JsonObject { ["codec_name"] = codec, ["color_transfer"] = transfer };
                        var metadata = new JsonObject();
                        foreach (var pair in entries)
                            metadata[pair.Key] = pair.Value;
                        var expected = MediaProbe.Classify(video, metadata, color is null ? null : (color.SchemaName, color.Mode));
                        var actual = NativeMedia.Classify(new(codec, transfer, entries, color));
                        check(Kind(actual.Kind) == expected.Kind && Profile(actual.Profile) == expected.Profile,
                            $"Source classification differs: {codec}/{transfer}/{color}/{metadata.ToJsonString()}");
                        check(actual.TagIndex is null || actual.TagIndex is >= 0 && actual.TagIndex < entries.Length,
                            "Classification evidence index points outside input tags.");
                    }
        foreach (uint compatibility in new uint[] { 0, 1, 2, 4, 6, 7 })
        {
            var actual = NativeMedia.Classify(new("prores_raw", "bt709", Tags("com.xiaomi.record_log", "1"),
                new(DjiSchema.Pocket3, 19), new(8, compatibility)));
            check(actual.Kind == (compatibility == 4 ? MediaKind.Hlg : MediaKind.Pq)
                && actual.Profile == CameraProfile.None && actual.Evidence == MediaEvidence.DolbyVision,
                "Dolby Vision did not override conflicting source evidence.");
        }
        check(NativeMedia.Classify(new("prores", "bt709", Tags("model", "iPhone 17 Pro"))).Profile == CameraProfile.None,
            "Camera name alone selected Apple Log.");
        foreach (var input in new[] {
            new NativeMediaInput("hevc", "bt709", Tags("gamma", new string('x', 65537))),
            new NativeMediaInput("hevc", "bt709", Enumerable.Repeat(KeyValuePair.Create("a", "b"), 1025).ToArray()),
            new NativeMediaInput("hevc", "bt709", Tags("gamma", "invalid\ud800text")) })
        {
            bool rejected = false;
            try
            {
                NativeMedia.Classify(input);
            }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Managed classification accepted invalid or over-budget tags.");
        }
    }

    private static string Kind(MediaKind kind) => kind switch
    {
        MediaKind.Sdr => "sdr",
        MediaKind.Hlg => "hlg",
        MediaKind.Pq => "pq",
        MediaKind.Log => "log",
        MediaKind.Raw => "raw",
        _ => "unknown"
    };
    private static string? Profile(CameraProfile profile) => profile switch
    {
        CameraProfile.DjiPocket3 => "pocket3",
        CameraProfile.SamsungLog => "samsung",
        CameraProfile.MiLog => "milog",
        CameraProfile.AppleLog => "applelog",
        CameraProfile.AppleLog2 => "applelog2",
        _ => null
    };

    internal static byte[] Integer(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 127);
            value >>= 7;
            bytes.Add((byte)(next | (value == 0 ? 0 : 128)));
        } while (value != 0);
        return bytes.ToArray();
    }
    private static byte[] Message(byte key, byte[] value) => [(byte)(key * 8 + 2), .. Integer((ulong)value.Length), .. value];
    private static byte[] DjiPacket(string schema, byte[] color) => [.. Message(1, Message(1, Message(1, Encoding.ASCII.GetBytes(schema)))),
        .. Message(2, Message(4, color))];
    private static DjiColor? ReferenceDji(byte[] packet)
    {
        try
        {
            var message = MediaProbe.ProtobufFields(packet);
            var header = MediaProbe.ProtobufFields((byte[])MediaProbe.ProtobufFields((byte[])message[1])[1]);
            string schema = Encoding.ASCII.GetString((byte[])header[1]);
            var id = schema switch
            {
                "dvtm_PP-101.proto" => DjiSchema.Pocket3,
                "dvtm_ac206.proto" => DjiSchema.Ac206,
                "dvtm_oq101.proto" => DjiSchema.Oq101,
                _ => DjiSchema.Unknown
            };
            if (id == DjiSchema.Unknown)
                return null;
            var color = MediaProbe.ProtobufFields((byte[])MediaProbe.ProtobufFields((byte[])message[2])[4]);
            return new(id, color.TryGetValue(1, out var mode) ? (ulong)mode : 0);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or KeyNotFoundException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static void BinaryMetadata(Action<bool, string> check)
    {
        foreach (var schema in new[] { "dvtm_PP-101.proto", "dvtm_ac206.proto", "dvtm_oq101.proto", "unrecognized.proto" })
            foreach (ulong mode in new ulong[] { 0, 1, 2, 9, 19, 22, 999, ulong.MaxValue })
            {
                var packet = DjiPacket(schema, [8, .. Integer(mode), 8, 9]);
                var status = NativeMedia.ParseDjiColor(packet, out var actual, out _);
                check(actual == ReferenceDji(packet) && status == (schema == "unrecognized.proto" ? NativeStatus.NotFound : NativeStatus.Ok),
                    "DJI schema, mode or duplicate-field precedence differs.");
            }
        var valid = DjiPacket("dvtm_PP-101.proto", [8, 19]);
        var random = new Random(4481);
        for (int round = 0; round < 1200; ++round)
        {
            var bytes = round < valid.Length ? valid[..round] : (byte[])valid.Clone();
            if (round >= valid.Length)
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            var status = NativeMedia.ParseDjiColor(bytes, out var actual, out _);
            check(actual == ReferenceDji(bytes) && (actual is null || status == NativeStatus.Ok), "DJI malformed-packet fallback differs.");
        }
        foreach (byte profile in new byte[] { 44, 66, 83, 86, 100, 110, 118, 122, 128, 134, 135, 138, 139, 144, 244 })
            foreach (byte tail in new byte[] { 0xc0, 0xa0, 0x90, 0x80, 0, 1 })
            {
                byte[] nal = [0x67, profile, 0, 40, tail];
                foreach (byte[] extra in new byte[][] { [0, 0, 1, .. nal], [0, 0, 0, 1, .. nal],
                [1, profile, 0, 40, 255, 225, 0, (byte)nal.Length, .. nal],
                [0,0,1,0x69,0x10,0,0,1,..nal,0,0,1,0x68,0] })
                    CheckAvc(check, extra);
            }
        for (int round = 0; round < 1000; ++round)
        {
            var bytes = new byte[random.Next(65)];
            random.NextBytes(bytes);
            CheckAvc(check, bytes);
        }
        byte[] mono = [1, 100, 0, 40, 255, 225, 0, 5, 0x67, 100, 0, 40, 0xc0];
        for (int length = 0; length < mono.Length; ++length)
            CheckAvc(check, mono[..length]);
        CheckAvc(check, [0, 0, 1, 0x67, 100, 0, 0, 3, 0xc0]);
        check(NativeMedia.ParseDjiColor(new byte[1048577], out _, out _) == NativeStatus.InvalidArgument,
            "Oversized DJI packet reached Rust.");
    }
    private static void CheckAvc(Action<bool, string> check, byte[] extra)
    {
        var video = new JsonObject { ["codec_name"] = "h264", ["extradata"] = Dump(extra) };
        bool expected = MediaProbe.AvcMonochrome(video);
        var status = NativeMedia.AvcIsMonochrome(extra, out bool actual, out _);
        check(actual == expected && (!actual || status == NativeStatus.Ok), "AVC monochrome detection differs.");
    }

    internal static string Dump(byte[] bytes)
    {
        var result = new StringBuilder("\n");
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            result.Append(offset.ToString("x8")).Append(": ");
            for (int index = offset; index < Math.Min(offset + 16, bytes.Length); ++index)
            {
                result.Append(bytes[index].ToString("x2"));
                if ((index - offset) % 2 == 1)
                    result.Append(' ');
            }
            result.Append("  ABCDEF0123: ASCII preview\r\n");
        }
        return result.ToString();
    }
    private static void HexDumps(Action<bool, string> check)
    {
        var random = new Random(6801);
        for (int size = 0; size < 513; ++size)
        {
            var payload = new byte[size];
            random.NextBytes(payload);
            string dump = Dump(payload);
            check(NativeMedia.DecodeProbeHex(dump).AsSpan().SequenceEqual(MediaProbe.ReadHexDump(dump)),
                "Hex dump decoder included offsets/ASCII or changed payload bytes.");
        }
    }

    internal static async Task<bool> SoftwareSamplesAsync(Action<bool, string> check, string ffmpeg, string ffprobe, string root)
    {
        bool cleanupConfirmed = true;
        foreach (string transfer in new[] { "bt709", "arib-std-b67", "smpte2084" })
        {
            string path = Path.Combine(root, "color-" + transfer + ".mp4");
            bool hdr = transfer != "bt709";
            string filter = $"format=yuv420p10le,setparams=color_primaries={(hdr ? "bt2020" : "bt709")}:color_trc={transfer}:colorspace={(hdr ? "bt2020nc" : "bt709")}:range=limited";
            var encoded = await NativeProcess.RunAsync(new(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=128x72:r=10:d=0.2",
                "-vf", filter, "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p10le", "-color_trc", transfer,
                "-color_primaries", hdr ? "bt2020" : "bt709", "-colorspace", hdr ? "bt2020nc" : "bt709",
                "-movflags", "+write_colr", "-n", path]));
            cleanupConfirmed &= encoded.CleanupConfirmed;
            check(encoded.Status == NativeStatus.Ok, "Source color fixture failed: " + encoded.ErrorText);
            if (encoded.Status != NativeStatus.Ok)
                return cleanupConfirmed;
            var probe = await NativeProcess.RunAsync(new(ffprobe,
                ["-v", "error", "-show_streams", "-show_format", "-show_data", "-of", "json", path], OutputCapacity: 65536));
            cleanupConfirmed &= probe.CleanupConfirmed;
            check(probe.Status == NativeStatus.Ok, "Source color fixture probe failed: " + probe.Diagnostic);
            if (probe.Status != NativeStatus.Ok)
                return cleanupConfirmed;
            var data = JsonNode.Parse(probe.Output)!;
            var video = data["streams"]![0]!.AsObject();
            var actual = NativeMedia.Classify(new(video.Text("codec_name"), video.Text("color_transfer"), []));
            var expected = transfer switch
            {
                "bt709" => MediaKind.Sdr,
                "arib-std-b67" => MediaKind.Hlg,
                _ => MediaKind.Pq
            };
            check(actual.Kind == expected && actual.Profile == CameraProfile.None,
                $"Encoded transfer metadata differs: requested={transfer}, reported={video.Text("color_transfer")}, kind={actual.Kind}.");
            check(NativeMedia.DecodeProbeHex(video.Text("extradata")).AsSpan().SequenceEqual(MediaProbe.ReadHexDump(video.Text("extradata"))),
                "Native hex decoder changed real ffprobe extradata.");
            var inspectedSource = await NativeProbe.RunAsync(new(ffprobe, path));
            cleanupConfirmed &= inspectedSource.Snapshot.CleanupConfirmed;
            check(inspectedSource.Status == NativeStatus.Ok, "Native source orchestration failed: " + inspectedSource.Diagnostic);
            if (inspectedSource.Status == NativeStatus.Ok)
            {
                ProbeChecks.Compare(check, inspectedSource, await MediaProbe.InspectAsync(new(ffmpeg, ffprobe), path));
                cleanupConfirmed &= await PlanChecks.SoftwareEncodeAsync(check, ffmpeg, ffprobe, path,
                    Path.Combine(root, "planned-" + transfer + ".mkv"), inspectedSource.Data, hdr);
            }
        }
        string gray = Path.Combine(root, "monochrome-h264.mp4");
        var avc = await NativeProcess.RunAsync(new(ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=128x72:r=10:d=0.2",
            "-vf", "format=gray", "-c:v", "libx264", "-threads", "1", "-pix_fmt", "gray", "-n", gray]));
        cleanupConfirmed &= avc.CleanupConfirmed;
        check(avc.Status == NativeStatus.Ok, "Monochrome AVC fixture failed: " + avc.ErrorText);
        if (avc.Status != NativeStatus.Ok)
            return cleanupConfirmed;
        var inspected = await NativeProcess.RunAsync(new(ffprobe,
            ["-v", "error", "-show_streams", "-show_data", "-of", "json", gray], OutputCapacity: 65536));
        cleanupConfirmed &= inspected.CleanupConfirmed;
        check(inspected.Status == NativeStatus.Ok, "Monochrome AVC fixture probe failed.");
        if (inspected.Status != NativeStatus.Ok)
            return cleanupConfirmed;
        var avcVideo = JsonNode.Parse(inspected.Output)!["streams"]![0]!.AsObject();
        var bytes = NativeMedia.DecodeProbeHex(avcVideo.Text("extradata"));
        check(NativeMedia.AvcIsMonochrome(bytes, out bool mono, out _) == NativeStatus.Ok && mono
            && MediaProbe.AvcMonochrome(avcVideo), "Real AVC SPS monochrome detection differs.");
        var inspectedGray = await NativeProbe.RunAsync(new(ffprobe, gray));
        cleanupConfirmed &= inspectedGray.Snapshot.CleanupConfirmed;
        check(inspectedGray.Status == NativeStatus.Ok, "Native monochrome inspection failed: " + inspectedGray.Diagnostic);
        if (inspectedGray.Status == NativeStatus.Ok)
            ProbeChecks.Compare(check, inspectedGray, await MediaProbe.InspectAsync(new(ffmpeg, ffprobe), gray));
        Console.WriteLine("Encoded SDR/HLG/PQ source classification and monochrome AVC metadata checks passed.");
        return cleanupConfirmed;
    }
}
