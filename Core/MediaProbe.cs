using System.Text;
using System.Text.Json.Nodes;
using LumaRecode.Native;

namespace VideoConverter.Core;

/// <summary>Calls shared source inspection and supplies detached presentation data.</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> InspectAsync(MediaTools tools, string path, CancellationToken cancellation = default)
    {
        var result = await NativeProbe.RunAsync(new(Path.GetFullPath(tools.Ffprobe), Path.GetFullPath(path)), cancellation).ConfigureAwait(false);
        NativeBackend.Check(result.Status, result.Diagnostic, result.Snapshot.CleanupConfirmed, cancellation);
        var media = NativeBackend.FromSnapshot(result.Data);
        if (media.Kind == "raw")
        {
            // RAW calibration is still provided by the existing development
            // stage; the source inspection and timeline already use the core.
            media.Video["_raw"] = await RawDevelopment.InspectAsync(tools, media.Path, media.Video.Integer("index"), cancellation).ConfigureAwait(false);
            media.Video["color_range"] = "pc";
        }
        return media;
    }

    public static async Task<JsonObject> ReadAsync(MediaTools tools, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        var result = await ProcessRunner.CaptureAsync(tools.Ffprobe, new[] { "-v", "error" }.Concat(arguments), cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(result.Error);
        }

        return JsonNode.Parse(NativeProbe.NormalizeJson(Encoding.UTF8.GetBytes(result.Output)))!.AsObject();
    }

    public static byte[] ReadHexDump(string dump) => NativeMedia.DecodeProbeHex(dump);

    public static bool AvcMonochrome(JsonObject video) => video.Text("codec_name") == "h264"
        && NativeMedia.AvcIsMonochrome(ReadHexDump(video.Text("extradata")), out bool mono, out _) == NativeStatus.Ok && mono;

    public static (string Kind, string? Profile, string Evidence) Classify(JsonObject video, JsonObject tags, (string Schema, ulong Mode)? dji = null)
    {
        var color = dji is { } value ? new DjiColor(value.Schema switch
        {
            "dvtm_PP-101.proto" => DjiSchema.Pocket3,
            "dvtm_ac206.proto" => DjiSchema.Ac206,
            "dvtm_oq101.proto" => DjiSchema.Oq101,
            _ => DjiSchema.Unknown
        }, value.Mode) : null;
        var classification = NativeMedia.Classify(new(video.Text("codec_name"), video.Text("color_transfer"),
            tags.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.ToString() ?? "")).ToArray(), color));
        var snapshot = new JsonObject
        {
            ["video"] = video.DeepClone(),
            ["tags"] = tags.DeepClone(),
            ["classification"] = new JsonObject { ["evidence"] = (uint)classification.Evidence, ["tag_index"] = classification.TagIndex },
            ["dji_color"] = color is null ? null : new JsonObject { ["mode"] = color.Mode }
        };
        string? profile = classification.Profile switch
        {
            CameraProfile.DjiPocket3 => "pocket3",
            CameraProfile.SamsungLog => "samsung",
            CameraProfile.MiLog => "milog",
            CameraProfile.AppleLog => "applelog",
            CameraProfile.AppleLog2 => "applelog2",
            _ => null
        };
        return (classification.Kind.ToString().ToLowerInvariant(), profile, NativeBackend.Evidence(snapshot));
    }
    /// <summary>Creates a detached presentation snapshot; detailed mode also includes original probe evidence.</summary>
    public static JsonObject Describe(MediaInfo media, bool detailed = false)
    {
        static JsonObject Select(JsonObject source, string[] fields)
        {
            var result = new JsonObject();
            foreach (var name in fields)
            {
                if (source.ContainsKey(name))
                {
                    result[name] = source[name]?.DeepClone();
                }
            }

            return result;
        }
        var (eligible, reason) = OutputPolicy.FlacEligibility(media);
        var result = new JsonObject
        {
            ["path"] = media.Path,
            ["kind"] = media.Kind,
            ["profile"] = media.Profile,
            ["dynamic_hdr"] = media.DynamicHdr,
            ["dynamic_hdr_supported"] = media.DolbyVision is not { } dv || DynamicHdr.SupportsDolbyVision(dv),
            ["dolby_vision"] = media.DolbyVision?.DeepClone(),
            ["hdr10plus"] = media.Hdr10Plus,
            ["raw"] = media.Raw?.DeepClone(),
            ["evidence"] = media.Evidence,
            ["duration"] = media.Duration,
            ["video"] = Select(media.Video, ["codec_name", "width", "height", "pix_fmt", "avg_frame_rate", "r_frame_rate", "color_range", "color_primaries", "color_transfer", "color_space", "profile", "bit_rate", "bits_per_raw_sample", "bits_per_sample"]),
            ["component_depths"] = new JsonArray(media.Pixel["components"].Objects().Select(c => (JsonNode?)JsonValue.Create(c.Integer("bit_depth"))).ToArray()),
            ["pixel_float"] = media.Pixel["flags"].Flag("float"),
            ["pixel_rgb"] = OutputPolicy.IsRgb(media),
            ["pixel_alpha"] = media.Pixel["flags"].Flag("alpha"),
            ["source_sampling"] = OutputPolicy.SourceSampling(media),
            ["source_depth"] = OutputPolicy.SourceDepth(media),
            ["allowed_sampling"] = JsonFields.Strings(OutputPolicy.SamplingChoices(media)),
            ["archive_sampling"] = OutputPolicy.DefaultSampling(media, "archive", "hevc"),
            ["source_color_range"] = OutputPolicy.SourceRange(media),
            ["range_inferred"] = media.Video.Text("color_range") is not ("pc" or "tv"),
            ["flac_eligible"] = eligible,
            ["flac_reason"] = reason,
            ["audio"] = new JsonArray(media.Audio.Select(s => (JsonNode?)Select(s, ["index", "codec_name", "profile", "channels", "channel_layout", "sample_rate", "bit_rate", "bits_per_raw_sample", "bits_per_sample", "sample_fmt"])).ToArray())
        };
        if (detailed)
        {
            result["streams"] = media.Streams.DeepClone();
            result["source_tags"] = media.Tags.DeepClone();
            result["first_frames"] = media.Frames.DeepClone();
        }
        return result;
    }
}
