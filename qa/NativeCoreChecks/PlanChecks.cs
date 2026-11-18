using System.Text;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;
using OutputPolicy = LumaRecode.Tests.Reference.ReferenceOutputPolicy;
using Transcoder = LumaRecode.Tests.Reference.ReferencePlanner;

internal static class PlanChecks
{
    internal static byte[] MediaJson(MediaInfo media) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["schema_version"] = 1,
        ["path"] = media.Path,
        ["video"] = media.Video.DeepClone(),
        ["frames"] = media.Frames.DeepClone(),
        ["streams"] = media.Streams.DeepClone(),
        ["pixel"] = media.Pixel.DeepClone(),
        ["tags"] = media.Tags.DeepClone(),
        ["kind"] = media.Kind,
        ["profile"] = media.Profile,
        ["duration"] = media.Duration
    }.ToJsonString());

    internal static NativePlanResult Plan(Transcoder baseline, MediaTools tools, MediaInfo media, EncodeOptions options,
        string destination, FrameWindow? window = null, bool motion = false, string size = "source", int preview = 0, double? seconds = null)
        => NativePlanner.Create(Request(baseline, tools, media, options, destination, window, motion, size, preview, seconds));

    internal static NativePlanRequest Request(Transcoder baseline, MediaTools tools, MediaInfo media, EncodeOptions options,
        string destination, FrameWindow? window = null, bool motion = false, string size = "source", int preview = 0, double? seconds = null)
        => new(MediaJson(media), Encoding.UTF8.GetBytes(options.ToJson().ToJsonString()),
            Encoding.UTF8.GetBytes(baseline.Config.ToJsonString()), tools.Ffmpeg, destination, baseline.Assets,
            Encoding.UTF8.GetBytes(baseline.MotionConfig.ToJsonString()), Seconds: seconds ?? 0,
            Flags: motion ? NativePlanFlags.MotionPhoto : NativePlanFlags.None, PreviewSize: (uint)preview,
            Window: window is null ? null : new((ulong)window.Start, (ulong)window.End, (ulong)window.SeekFrame,
                window.SeekSeconds, window.StartSeconds, window.EndSeconds), MotionSize: size);

    private static int differences;
    internal static async Task<bool> SoftwareEncodeAsync(Action<bool, string> check, string ffmpeg, string ffprobe,
        string source, string destination, byte[] sourceJson, bool hdr)
    {
        using var transaction = NativeOutputTransaction.Create(source, destination);
        bool cleaned = true;
        try
        {
            var assets = Path.Combine(Directory.GetCurrentDirectory(), "Assets");
            var request = new NativePlanRequest(sourceJson, Encoding.UTF8.GetBytes(new EncodeOptions(Purpose: "archive",
                Output: hdr ? "hdr" : "sdr").ToJson().ToJsonString()), File.ReadAllBytes(Path.Combine(assets, "recommended-presets.json")),
                ffmpeg, transaction.StagingPath, assets);
            var planned = NativePlanner.Create(request);
            check(planned.Status == NativeStatus.Ok, "Software HEVC native planning failed: " + planned.Diagnostic);
            if (planned.Status != NativeStatus.Ok)
            {
                return cleaned;
            }

            var plan = JsonNode.Parse(planned.Data)!.AsObject();
            var args = plan["arguments"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            var converted = await NativeProcess.RunAsync(new(args[0], args[1..], WorkingDirectory: assets));
            cleaned &= converted.CleanupConfirmed;
            check(converted.Status == NativeStatus.Ok, "Software HEVC native plan encode failed: " + converted.ErrorText);
            if (converted.Status != NativeStatus.Ok)
            {
                return cleaned;
            }

            var inspected = await NativeProbe.RunAsync(new(ffprobe, transaction.StagingPath));
            cleaned &= inspected.Snapshot.CleanupConfirmed;
            check(inspected.Status == NativeStatus.Ok, "Planned HEVC output inspection failed: " + inspected.Diagnostic);
            if (inspected.Status != NativeStatus.Ok)
            {
                return cleaned;
            }

            var verified = NativePlanner.VerifyOutput(request, inspected.Data);
            check(verified.Status == NativeStatus.Ok, "Planned HEVC native verification failed: " + verified.Diagnostic);
            if (verified.Status != NativeStatus.Ok)
            {
                return cleaned;
            }

            check(transaction.Seal(cleaned, out _) == NativeStatus.Ok && transaction.Commit(out _) == NativeStatus.Ok,
                "Verified HEVC output publication failed.");
        }
        finally
        {
            cleaned &= transaction.Cleanup(cleaned, out _) == NativeStatus.Ok;
        }
        return cleaned;
    }
    private static void Verify(Action<bool, string> check, Transcoder planner, MediaTools tools, string path, string destination)
    {
        var media = Source(path, "pq", null);
        var request = Request(planner, tools, media, new(Purpose: "archive", Output: "hdr"), destination);
        var encoded = MediaJson(media);
        var verified = NativePlanner.VerifyOutput(request, encoded);
        check(verified.Status == NativeStatus.Ok, "Matching HDR output failed native verification: " + verified.Diagnostic);
        foreach (string field in new[] { "width", "height", "pix_fmt", "codec_name", "color_range", "color_primaries" })
        {
            var changed = JsonNode.Parse(encoded)!.AsObject();
            changed["video"]![field] = field is "width" or "height" ? JsonValue.Create(500) : JsonValue.Create("wrong");
            check(NativePlanner.VerifyOutput(request, Encoding.UTF8.GetBytes(changed.ToJsonString())).Status == NativeStatus.InvalidData,
                "Mismatched output field accepted: " + field);
        }
        foreach (double delta in new[] { 0.00001, 0.01 })
        {
            var changed = JsonNode.Parse(encoded)!.AsObject();
            changed["video"]!["side_data_list"]![0]!["green_x"] = .17 + delta;
            var status = NativePlanner.VerifyOutput(request, Encoding.UTF8.GetBytes(changed.ToJsonString())).Status;
            check((status == NativeStatus.Ok) == (delta < .00002), "Static HDR quantization tolerance differs.");
        }
        media.Video["bits_per_raw_sample"] = 10;
        media.Video["pix_fmt"] = "yuv420p10le";
        media.Video["side_data_list"]!.AsArray().Add(new JsonObject { ["side_data_type"] = "HDR10+ Dynamic Metadata (SMPTE 2094-40)", ["average_maxrgb"] = "10/100" });
        request = Request(planner, tools, media, new(Purpose: "archive", Output: "hdr"), destination);
        encoded = MediaJson(media);
        check(NativePlanner.VerifyOutput(request, encoded).Status == NativeStatus.Ok, "Matching HDR10+ output rejected.");
        var lost = JsonNode.Parse(encoded)!.AsObject();
        lost["video"]!["side_data_list"]!.AsArray().RemoveAt(2);
        check(NativePlanner.VerifyOutput(request, Encoding.UTF8.GetBytes(lost.ToJsonString())).Status == NativeStatus.InvalidData,
            "Missing HDR10+ output metadata accepted.");
        var gray = Source(path, "sdr", null);
        gray.Video["pix_fmt"] = "gray10le";
        gray.Video["bits_per_raw_sample"] = 10;
        gray.Video["side_data_list"] = new JsonArray();
        request = Request(planner, tools, gray, new(Purpose: "archive", Codec: "avc"), destination);
        var mono = JsonNode.Parse(MediaJson(gray))!.AsObject();
        mono["video"]!["codec_name"] = "h264";
        mono["video"]!["color_primaries"] = "bt709";
        mono["video"]!["color_space"] = "bt709";
        mono["video"]!["pix_fmt"] = "yuv420p10le";
        mono["video"]!["_monochrome"] = true;
        check(NativePlanner.VerifyOutput(request, Encoding.UTF8.GetBytes(mono.ToJsonString())).Status == NativeStatus.Ok,
            "Matching monochrome AVC storage rejected.");
        mono["video"]!["bits_per_raw_sample"] = 8;
        mono["video"]!["pix_fmt"] = "yuv420p";
        check(NativePlanner.VerifyOutput(request, Encoding.UTF8.GetBytes(mono.ToJsonString())).Status == NativeStatus.InvalidData,
            "Monochrome AVC flag bypassed requested output precision.");
    }
    internal static void Compare(Action<bool, string> check, Transcoder baseline, MediaTools tools, MediaInfo media,
        EncodeOptions options, string destination, FrameWindow? window = null, bool motion = false,
        string size = "source", int preview = 0, double? seconds = null)
    {
        EncodingPlan? expected = null;
        try
        {
            expected = baseline.Plan(tools, media, options, destination, seconds, window, motion, size, preview);
        }
        catch (InvalidDataException) { }
        var result = Plan(baseline, tools, media, options, destination, window, motion, size, preview, seconds);
        check((result.Status == NativeStatus.Ok) == (expected is not null),
            $"Native plan acceptance differs: {options.ToJson()}, {media.Kind}: {result.Diagnostic}");
        if (expected is null || result.Status != NativeStatus.Ok)
        {
            return;
        }

        var actual = JsonNode.Parse(result.Data)!.AsObject();
        var actualArgs = actual["arguments"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        bool same = actualArgs.SequenceEqual(expected.Arguments);
        check(same, $"Native argument vector differs: {options.ToJson()}, {media.Kind}");
        if (!same && differences++ < 8)
        {
            foreach (var (value, index) in actualArgs.Select((v, i) => (v, i)))
            {
                if (index >= expected.Arguments.Length || value != expected.Arguments[index])
                {
                    Console.Error.WriteLine($"  argument {index}: native={value}\n  expected={(index < expected.Arguments.Length ? expected.Arguments[index] : "<absent>")}");
                }
            }
        }
        check(JsonNode.DeepEquals(actual["colors"], expected.Colors), "Native expected color tags differ.");
        check(JsonNode.DeepEquals(actual["options"], expected.Options.ToJson()), "Native resolved choices differ.");
        check(actual["qsv_timing_compatibility"]!.GetValue<bool>() == expected.QsvTimingCompatibility
            && actual["uses_software_decoding"]!.GetValue<bool>() == expected.UsesSoftwareDecoding
            && actual["working_directory"]?.GetValue<string>() == expected.WorkingDirectory,
            "Native execution route/working directory differs.");
    }

    internal static void Run(Action<bool, string> check)
    {
        var assets = Path.Combine(Directory.GetCurrentDirectory(), "Assets");
        var baseline = new Transcoder(assets);
        var tools = new MediaTools(Path.Combine(assets, "ffmpeg"), Path.Combine(assets, "ffprobe"));
        var sourcePath = Path.Combine(assets, "source.mkv");
        var destination = Path.Combine(assets, "output.mp4");
        foreach (string kind in new[] { "sdr", "hlg", "pq", "log", "unknown" })
        {
            foreach (string purpose in new[] { "share", "archive" })
            {
                foreach (string codec in new[] { "hevc", "avc", "av1" })
                {
                    foreach (int depth in new[] { 8, 10, 12 })
                    {
                        foreach (string sampling in new[] { "420", "422", "444", "gray", "rgb" })
                        {
                            var video = new JsonObject
                            {
                                ["index"] = 2,
                                ["codec_name"] = "hevc",
                                ["pix_fmt"] = sampling == "rgb" ? "gbrp16le" : sampling == "gray" ? "gray16le" : "yuv" + sampling + "p16le",
                                ["bits_per_raw_sample"] = depth,
                                ["avg_frame_rate"] = "30000/1001",
                                ["r_frame_rate"] = "30/1",
                                ["color_primaries"] = kind is "hlg" or "pq" or "log" ? "bt2020" : "bt709",
                                ["color_transfer"] = kind == "hlg" ? "arib-std-b67" : kind == "pq" ? "smpte2084" : "bt709",
                                ["color_space"] = kind is "hlg" or "pq" or "log" ? "bt2020nc" : "bt709",
                                ["color_range"] = "tv"
                            };
                            var media = new MediaInfo(sourcePath, video, [], [], kind, kind == "log" ? "milog" : null, "fixture", 3, [], new());
                            var options = new EncodeOptions(Purpose: purpose, Codec: codec, Output: kind is "hlg" or "pq" or "log" ? "hdr" : "sdr",
                                Transform: kind == "log" ? "log-hdr" : "none", Profile: kind == "log" ? "milog" : null);
                            Compare(check, baseline, tools, media, options, destination);
                            CheckSource(check, media);
                        }
                    }
                }
            }
        }
        // Full option routes: LUT profiles, static/dynamic HDR, rotation,
        // multiple audio tracks, float RGB, precision and source range.
        foreach (string kind in new[] { "sdr", "hlg", "pq", "log" })
        {
            foreach (string profile in new[] { "pocket3", "samsung", "milog", "applelog", "applelog2" })
            {
                foreach (string purpose in new[] { "share", "archive" })
                {
                    foreach (string codec in new[] { "hevc", "avc", "av1" })
                    {
                        foreach (string audio in new[] { "auto", "copy", "aac", "opus", "flac" })
                        {
                            var media = Source(sourcePath, kind, profile);
                            foreach (string output in new[] { "sdr", "hdr" })
                            {
                                var options = new EncodeOptions(Purpose: purpose, Codec: codec, Output: output,
                                    Transform: kind == "log" ? "log-" + output : output == "sdr" && kind != "sdr" ? "hdr-sdr" : "none",
                                    Profile: profile, Audio: audio, ColorRange: "full", Container: "mkv");
                                Compare(check, baseline, tools, media, options, Path.ChangeExtension(destination, ".mkv"));
                            }
                        }
                    }
                }
            }
        }

        var window = new FrameWindow(7, 29, 5, .625, .675, 1.225);
        foreach (string kind in new[] { "sdr", "hlg", "pq", "log" })
        {
            foreach (bool motion in new[] { false, true })
            {
                foreach (int preview in new[] { 0, 1024 })
                {
                    foreach (string codec in new[] { "hevc", "avc", "av1" })
                    {
                        foreach (string audio in new[] { "auto", "copy", "aac", "flac" })
                        {
                            var media = Source(sourcePath, kind, "applelog2");
                            var options = new EncodeOptions(Purpose: "archive", Codec: codec, Output: "sdr", Audio: audio,
                                Transform: kind == "log" ? "log-sdr" : kind == "sdr" ? "none" : "hdr-sdr", Profile: "applelog2");
                            Compare(check, baseline, tools, media, options, destination, window, motion, "720", preview, 1.125);
                        }
                    }
                }
            }
        }

        foreach (string type in new[] { "hdr10plus", "dovi5", "dovi8", "dovi10", "dovi7", "rotation" })
        {
            foreach (string output in new[] { "sdr", "hdr" })
            {
                foreach (string dynamic in new[] { "preserve", "standard" })
                {
                    foreach (string codec in new[] { "hevc", "avc", "av1" })
                    {
                        var media = Source(sourcePath, "pq", null);
                        media.Video["bits_per_raw_sample"] = 10;
                        var side = media.Video["side_data_list"]!.AsArray();
                        if (type == "hdr10plus")
                        {
                            side.Add(new JsonObject { ["side_data_type"] = "HDR10+ Dynamic Metadata (SMPTE 2094-40)" });
                        }
                        else if (type == "rotation")
                        {
                            side.Add(new JsonObject { ["side_data_type"] = "Display Matrix", ["rotation"] = 90 });
                        }
                        else
                        {
                            side.Add(new JsonObject
                            {
                                ["side_data_type"] = "DOVI configuration record",
                                ["dv_profile"] = int.Parse(type[4..]),
                                ["rpu_present_flag"] = 1,
                                ["bl_present_flag"] = 1,
                                ["el_present_flag"] = 0,
                                ["dv_bl_signal_compatibility_id"] = type == "dovi5" ? 0 : 1
                            });
                        }

                        Compare(check, baseline, tools, media, new(Purpose: "archive", Codec: codec, Output: output,
                            Transform: output == "sdr" ? "hdr-sdr" : "none", DynamicHdr: dynamic), destination);
                    }
                }
            }
        }

        Flac(check, sourcePath);
        var hardwareSdr = Source(sourcePath, "sdr", null);
        var hardwareHdr = Source(sourcePath, "hlg", null);
        foreach (var (backend, codec, depth, encoder, selector, value) in new[]
        {
            ("nvenc", "hevc", "10", "hevc_nvenc", "-rc", "vbr"),
            ("nvenc", "av1", "10", "av1_nvenc", "-preset", "p4"),
            ("amf", "hevc", "8", "hevc_amf", "-rc", "qvbr"),
            ("amf", "av1", "10", "av1_amf", "-rc", "cqp"),
            ("amf", "hevc", "10", "hevc_amf", "-bitdepth", "10"),
            ("mf", "avc", "8", "h264_mf", "-hw_encoding", "1"),
            ("videotoolbox", "avc", "8", "h264_videotoolbox", "-allow_sw", "0"),
            ("videotoolbox", "hevc", "8", "hevc_videotoolbox", "-q:v", "65"),
            ("videotoolbox", "hevc", "10", "hevc_videotoolbox", "-allow_sw", "0")
        })
        {
            var result = Plan(baseline, tools, depth == "10" ? hardwareHdr : hardwareSdr,
                new(Codec: codec, Hardware: backend, BitDepth: depth, Output: depth == "10" ? "hdr" : "sdr"), destination);
            if (result.Status != NativeStatus.Ok)
            {
                check(false, $"Hardware planning failed for {backend}/{codec}/{depth}: {result.Diagnostic}");
                continue;
            }
            var args = JsonNode.Parse(result.Data)!["arguments"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            int option = Array.IndexOf(args, selector);
            check(args[Array.IndexOf(args, "-c:v") + 1] == encoder && option >= 0 && args[option + 1] == value &&
                !args.Contains("-hwaccel") && !args.Contains("-low_power") && !args.Contains("-global_quality:v") &&
                args[Array.IndexOf(args, "-vf") + 1].Contains(depth == "10" ? "format=p010le" : "format=nv12", StringComparison.Ordinal),
                $"Hardware plan mixed encoder arguments for {backend}/{codec}/{depth}.");
        }
        check(Plan(baseline, tools, hardwareHdr, new(Output: "hdr", Hardware: "mf"), destination).Status != NativeStatus.Ok,
            "Unlisted Media Foundation Main 10 path was accepted.");
        check(Plan(baseline, tools, hardwareSdr, new(Codec: "av1", Hardware: "videotoolbox"), destination).Status != NativeStatus.Ok,
            "Unlisted VideoToolbox AV1 encode path was accepted.");
        check(Plan(baseline, tools, hardwareSdr, new(Hardware: "unknown"), destination).Status != NativeStatus.Ok,
            "Unknown hardware backend was accepted.");
        Verify(check, baseline, tools, sourcePath, destination);
        foreach (string output in new[] { "sdr", "hdr" })
        {
            foreach (int preview in new[] { 0, 1024 })
            {
                foreach (string purpose in new[] { "share", "archive" })
                {
                    foreach (var axes in new[] { (1, 0, 0, 1), (-1, 0, 0, 1), (1, 0, 0, -1), (-1, 0, 0, -1), (0, -1, 1, 0), (0, 1, -1, 0), (0, 1, 1, 0), (0, -1, -1, 0) })
                    {
                        var media = Source(sourcePath, "raw", null);
                        media.Video["codec_name"] = "prores_raw";
                        media.Video["pix_fmt"] = "bayer_rggb16le";
                        media.Video["color_range"] = "pc";
                        media.Video["_raw"] = new JsonObject
                        {
                            ["decoder_version_supported"] = true,
                            ["decoder_supported"] = true,
                            ["width"] = 128,
                            ["height"] = 72,
                            ["crop"] = new JsonArray(1, 1, 1, 1),
                            ["matrix"] = new JsonArray(0.0009765625, 0, 0, 0, 0, 0.0009765625, 0, 0, 0, 0, 0.0009765625, 0)
                        };
                        media.Video["side_data_list"] = new JsonArray(new JsonObject
                        {
                            ["side_data_type"] = "Display Matrix",
                            ["displaymatrix"] = $"00000000: {axes.Item1 * 65536} {axes.Item2 * 65536} 0\n00000001: {axes.Item3 * 65536} {axes.Item4 * 65536} 0\n00000002: 0 0 1073741824"
                        });
                        Compare(check, baseline, tools, media, new(Purpose: purpose, Output: output), destination, preview: preview);
                    }
                }
            }
        }
    }

    private static MediaInfo Source(string path, string kind, string? profile)
        => new(path, new JsonObject
        {
            ["index"] = 0,
            ["width"] = 1280,
            ["height"] = 720,
            ["codec_name"] = "hevc",
            ["pix_fmt"] = "yuv420p12le",
            ["bits_per_raw_sample"] = 12,
            ["avg_frame_rate"] = "24000/1001",
            ["r_frame_rate"] = "24000/1001",
            ["color_range"] = "tv",
            ["color_transfer"] = kind == "hlg" ? "arib-std-b67" : kind == "pq" ? "smpte2084" : "bt709",
            ["color_primaries"] = "bt2020",
            ["color_space"] = "bt2020nc",
            ["_format_start_time"] = "0.125",
            ["side_data_list"] = new JsonArray(new JsonObject
            {
                ["side_data_type"] = "Mastering display metadata",
                ["green_x"] = "8500/50000",
                ["green_y"] = "39850/50000",
                ["blue_x"] = "6550/50000",
                ["blue_y"] = "2300/50000",
                ["red_x"] = "35400/50000",
                ["red_y"] = "14600/50000",
                ["white_point_x"] = "15635/50000",
                ["white_point_y"] = "16450/50000",
                ["max_luminance"] = "10000000/10000",
                ["min_luminance"] = "50/10000"
            }, new JsonObject { ["side_data_type"] = "Content light level metadata", ["max_content"] = 1000, ["max_average"] = 400 })
        }, [], [], kind, profile, "fixture", 3,
        new JsonArray(new JsonObject { ["codec_type"] = "audio", ["codec_name"] = "pcm_s24le", ["channels"] = 2, ["channel_layout"] = "stereo" },
            new JsonObject { ["codec_type"] = "audio", ["codec_name"] = "flac", ["bits_per_raw_sample"] = 24, ["channels"] = 6, ["channel_layout"] = "5.1" }), new());

    private static void CheckSource(Action<bool, string> check, MediaInfo media)
    {
        var status = NativeMedia.ReadSourceFormat(MediaJson(media), out var format, out var diagnostic);
        var sampling = format.Sampling switch
        {
            Sampling.Yuv420 => "420",
            Sampling.Yuv422 => "422",
            Sampling.Yuv444 or Sampling.Rgb => "444",
            Sampling.Gray => "gray",
            Sampling.Yuv440 => "440",
            Sampling.Yuv411 => "411",
            Sampling.Yuv410 => "410",
            _ => "unknown"
        };
        check(status == NativeStatus.Ok && format.BitDepth == OutputPolicy.SourceDepth(media) && sampling == OutputPolicy.SourceSampling(media)
            && format.ColorRange == (OutputPolicy.SourceRange(media) == "full" ? ColorRange.Full : ColorRange.Limited),
            "Native source format derivation differs: " + diagnostic);
    }

    private static void Flac(Action<bool, string> check, string path)
    {
        foreach (string codec in new[] { "aac", "opus", "flac", "alac", "ape", "pcm_s16le", "pcm_u8", "pcm_s24be_planar", "pcm_f32le", "dts", "pcm_s24oops" })
        {
            foreach (int channels in new[] { 0, 1, 2, 3, 6, 8, 9 })
            {
                foreach (string layout in new[] { "", "unknown", "mono", "stereo", "5.1", "7.1" })
                {
                    foreach (string sample in new[] { "s32", "fltp", "dbl" })
                    {
                        var stream = new JsonObject
                        {
                            ["codec_type"] = "audio",
                            ["codec_name"] = codec,
                            ["channels"] = channels,
                            ["channel_layout"] = layout,
                            ["sample_fmt"] = sample,
                            ["bits_per_raw_sample"] = 24,
                            ["profile"] = "DTS-HD MA"
                        };
                        var media = Source(path, "sdr", null) with
                        {
                            Streams = new JsonArray(stream)
                        };
                        var expected = OutputPolicy.FlacEligibility(media).Eligible;
                        var status = NativeMedia.FlacDepth(Encoding.UTF8.GetBytes(stream.ToJsonString()), out var depth, out _);
                        check((status == NativeStatus.Ok) == expected && (!expected || depth == 24), "Native lossless FLAC eligibility differs.");
                    }
                }
            }
        }

        foreach (string pixel in new[] { "rgba", "gbrap16le", "yuv422p10le", "yuv440p", "yuv411p", "yuv410p", "nv12", "p010le", "ya16le", "unknown" })
        {
            var media = Source(path, "sdr", null);
            media.Video["pix_fmt"] = pixel;
            media.Video.Remove("bits_per_raw_sample");
            CheckSource(check, media);
        }
    }
}
