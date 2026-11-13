using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

/// <summary>Collects stream, first-frame and camera metadata into one source inspection snapshot.</summary>
public static class MediaProbe
{
    // Format descriptors depend on the ffprobe build, not on individual files.
    // Cached nodes are read-only after publication and never reparented.
    private static readonly ConcurrentDictionary<string, JsonObject> pixelFormats = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<JsonObject> ReadAsync(MediaTools tools, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        var result = await ProcessRunner.CaptureAsync(tools.Ffprobe, new[] { "-v", "error" }.Concat(arguments), cancellation);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(result.Error) ? "无法读取视频。" : result.Error.Trim());
        }

        // ffprobe emits repeated JSON members for HDR10+ percentiles, MaxSCL
        // and curve anchors. Normalize those members to arrays before JsonNode
        // builds its key index; otherwise a valid HDR10+ frame throws on access.
        using var document = JsonDocument.Parse(result.Output);
        return ReadProbeValue(document.RootElement) as JsonObject ?? throw new InvalidDataException("Invalid ffprobe response.");
    }

    private static JsonNode? ReadProbeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            var repeated = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                var node = ReadProbeValue(property.Value);
                if (!result.ContainsKey(property.Name))
                {
                    result[property.Name] = node;
                }
                else if (repeated.Contains(property.Name))
                {
                    result[property.Name]!.AsArray().Add(node);
                }
                else
                {
                    var previous = result[property.Name];
                    result.Remove(property.Name);
                    result[property.Name] = new JsonArray(previous, node);
                    repeated.Add(property.Name);
                }
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return new JsonArray(value.EnumerateArray().Select(ReadProbeValue).ToArray());
        }
        return value.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(value.Clone());
    }

    /// <summary>Inspects the first playable video stream, all audio tracks and first-frame side data.</summary>
    /// <remarks>Attached cover images are ignored. First-frame color tags fill only unspecified stream fields.</remarks>
    public static async Task<MediaInfo> InspectAsync(MediaTools tools, string path, CancellationToken cancellation = default)
    {
        path = Path.GetFullPath(path);
        var data = await ReadAsync(tools, ["-show_streams", "-show_format", "-show_data", "-of", "json", path], cancellation);
        var streams = data["streams"] as JsonArray ?? [];
        var video = streams.Objects().Where(s => s.Text("codec_type") == "video" && !s["disposition"].Flag("attached_pic")).OrderByDescending(s => s.Text("codec_name") == "prores_raw").FirstOrDefault()
            ?? throw new InvalidDataException("文件中没有可用的视频轨。");
        video["_monochrome"] = AvcMonochrome(video);
        bool raw = video.Text("codec_name") == "prores_raw";
        if (raw)
        {
            video["_raw"] = await RawDevelopment.InspectAsync(tools, path, video.Integer("index"), cancellation);
            // The selected decoder owns CFA layout and its pixel-format label.
            // Keep that result so future FFmpeg Bayer layouts use this same path.
            video["color_range"] = "pc";
        }
        var first = raw ? new JsonObject() : await ReadAsync(tools, ["-select_streams", video.Text("index"), "-read_intervals", "%+#1", "-show_frames", "-of", "json", path], cancellation);
        var frames = first["frames"] as JsonArray ?? [];
        foreach (var name in new[] { "color_transfer", "color_primaries", "color_space", "color_range" })
        {
            if (JsonFields.Unspecified(video.Text(name)))
            {
                var value = frames.Objects().Select(f => f.Text(name)).FirstOrDefault(v => !JsonFields.Unspecified(v));
                if (value is not null)
                {
                    video[name] = value;
                }
            }
        }

        var tags = (data["format"]?["tags"]?.DeepClone() as JsonObject) ?? new();
        foreach (var pair in video["tags"] as JsonObject ?? new())
        {
            tags[pair.Key] = pair.Value?.DeepClone();
        }

        var (kind, profile, evidence) = Classify(video, tags, await DjiColorAsync(tools, path, streams, cancellation));
        var side = video["side_data_list"].Objects().Concat(frames.Objects().SelectMany(f => f["side_data_list"].Objects()));
        var dovi = side.FirstOrDefault(s => s.Text("side_data_type") == "DOVI configuration record");
        if (dovi is not null)
        {
            kind = dovi.Integer("dv_bl_signal_compatibility_id") == 4 ? "hlg" : "pq";
            profile = null;
            evidence = $"Dolby Vision Profile {dovi.Integer("dv_profile")}";
        }
        if (!pixelFormats.TryGetValue(tools.Ffprobe, out var formats))
        {
            var all = await ReadAsync(tools, ["-show_pixel_formats", "-of", "json"], cancellation);
            formats = new();
            foreach (var item in all["pixel_formats"].Objects())
            {
                formats[item.Text("name")] = item.DeepClone();
            }

            if (pixelFormats.Count >= 8)
            {
                pixelFormats.Clear();
            }

            pixelFormats.TryAdd(tools.Ffprobe, formats);
        }
        var duration = JsonFields.Number(data["format"].Text("duration"), JsonFields.Number(video.Text("duration")));
        return new(path, video, frames, tags, kind, profile, evidence, duration, streams, formats[video.Text("pix_fmt")] as JsonObject ?? new());
    }

    /// <summary>Prefers camera color-mode evidence over generic transfer-function container tags.</summary>
    /// <remarks>Only a recognized camera/schema pair selects a specific LUT automatically.</remarks>
    public static (string Kind, string? Profile, string Evidence) Classify(JsonObject video, JsonObject tags, (string Schema, ulong Mode)? dji = null)
    {
        if (video.Text("codec_name") == "prores_raw")
        {
            return ("raw", null, "ProRes RAW · 相机白平衡与色彩矩阵");
        }
        var lowered = new Dictionary<string, string>();
        foreach (var p in tags)
        {
            lowered[p.Key.ToLowerInvariant()] = (p.Value?.ToString() ?? "").ToLowerInvariant();
        }

        // Apple identifies the transfer function and primaries together. Never
        // infer Apple Log solely from an iPhone model, ProRes codec or .MOV suffix.
        foreach (var key in new[] { "com.apple.proapps.logcolorspace", "com.apple.proapps.color.logcolorspace" })
        {
            var value = lowered.GetValueOrDefault(key, "");
            if (value == "com.apple.apple-wide-gamut.apple-log")
            {
                return ("log", "applelog2", "Apple Log 2 · Apple Wide Gamut");
            }
            if (value == "com.apple.log.apple-log")
            {
                return ("log", "applelog", "Apple Log · BT.2020");
            }
        }

        if (lowered.GetValueOrDefault("com.xiaomi.record_log") is "1" or "true" or "yes")
        {
            return ("log", "milog", "小米相机元数据确认 Mi-Log");
        }

        if (lowered.GetValueOrDefault("com.samsung.android.logvideo") is { Length: > 0 } samsung && samsung is not ("0" or "false" or "no"))
        {
            return ("log", "samsung", "三星相机元数据确认 Samsung Log");
        }

        if (dji is { } color)
        {
            if (color.Mode == 19)
            {
                return color.Schema == "dvtm_PP-101.proto"
                ? ("log", "pocket3", "DJI 专用元数据确认 D-Log M") : ("log", null, "DJI 元数据为 D-Log M，请确认机型对应的曲线");
            }

            if (color.Mode is 1 or 2 or 22)
            {
                return ("log", null, $"DJI Log 标记 ColorMode={color.Mode}，请选择对应曲线");
            }

            if (color.Mode == 9)
            {
                return ("hlg", null, "DJI 专用元数据确认 HLG");
            }

            if (color.Mode == 0)
            {
                return ("sdr", null, "DJI 元数据为普通色彩模式");
            }
        }
        foreach (var (key, value) in lowered)
        {
            if (new[] { "colorgamma", "color_mode", "colormode", "log_profile", "gamma" }.Any(key.Contains))
            {
                var compact = Regex.Replace(value, "[^a-z0-9]", "");
                if (compact is "dlogm" or "djidlogm")
                {
                    return ("log", string.Join(' ', lowered.Values).Contains("pocket") ? "pocket3" : null, $"厂商色彩标记 {value}");
                }

                if (compact is "milog" or "xiaomilog" or "samsunglog")
                {
                    return ("log", compact == "samsunglog" ? "samsung" : "milog", $"厂商色彩标记 {value}");
                }

                if (compact.Contains("log"))
                {
                    return ("log", null, "读到了 Log 标记，具体曲线需要手动确认");
                }
            }
        }

        if (lowered.Any(p => p.Key.Contains("log") && p.Value is "1" or "true" or "yes" or "log"))
        {
            return ("log", null, "读到了 Log 开关，具体曲线需要手动确认");
        }

        return video.Text("color_transfer") switch
        {
            "arib-std-b67" => ("hlg", null, "视频传递曲线为 HLG"),
            "smpte2084" => ("pq", null, "视频传递曲线为 PQ"),
            "bt709" or "smpte170m" or "bt470m" or "bt470bg" or "gamma22" or "gamma28" or "iec61966-2-1" or "bt2020-10" or "bt2020-12" => ("sdr", null, "视频使用标准 SDR 传递曲线"),
            _ => ("unknown", null, "传递曲线未标明，请确认输入类型或选择 Log 转换")
        };
    }

    /// <summary>Decodes ffprobe's offset/hex/ASCII dump, excluding offsets and the ASCII display column.</summary>
    public static byte[] ReadHexDump(string dump)
    {
        var hex = new StringBuilder();
        foreach (var line in dump.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var values = line[(colon + 1)..].TrimStart();
            var end = values.IndexOf("  ", StringComparison.Ordinal);
            if (end >= 0)
            {
                values = values[..end];
            }

            foreach (var c in values)
            {
                if (!char.IsWhiteSpace(c))
                {
                    hex.Append(c);
                }
            }
        }
        return Convert.FromHexString(hex.ToString());
    }

    /// <summary>Reads bounded protobuf wire fields used by DJI camera packets.</summary>
    /// <remarks>Length-delimited fields remain byte arrays. Unknown wire types, overflow
    /// and truncation are rejected; duplicate scalar fields retain their first value.</remarks>
    public static Dictionary<int, object> ProtobufFields(byte[] bytes)
    {
        int position = 0;
        ulong Integer()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (position >= bytes.Length)
                {
                    throw new InvalidDataException("Incomplete metadata");
                }

                byte b = bytes[position++];
                if (shift == 63 && b > 1)
                {
                    throw new InvalidDataException("Invalid metadata integer");
                }

                value |= (ulong)(b & 127) << shift;
                if (b < 128)
                {
                    return value;
                }
            }
            throw new InvalidDataException("Invalid metadata integer");
        }
        var result = new Dictionary<int, object>();
        while (position < bytes.Length)
        {
            var key = Integer();
            int field = checked((int)(key >> 3)), wire = (int)(key & 7);
            if (field == 0)
            {
                throw new InvalidDataException("Invalid metadata field");
            }

            object value;
            if (wire == 0)
            {
                value = Integer();
            }
            else
            {
                int length = wire switch
                {
                    2 => checked((int)Integer()),
                    1 => 8,
                    5 => 4,
                    _ => throw new InvalidDataException("Unsupported metadata field")
                };
                if (length > bytes.Length - position)
                {
                    throw new InvalidDataException("Incomplete metadata field");
                }

                value = bytes[position..(position + length)];
                position += length;
            }
            result.TryAdd(field, value);
        }
        return result;
    }

    private static async Task<(string, ulong)?> DjiColorAsync(MediaTools tools, string path, JsonArray streams, CancellationToken cancellation)
    {
        var stream = streams.Objects().FirstOrDefault(s => s.Text("codec_tag_string") == "djmd");
        if (stream is null)
        {
            return null;
        }

        try
        {
            var packet = (await ReadAsync(tools, ["-select_streams", stream.Text("index"), "-read_intervals", "%+#1", "-show_packets", "-show_data", "-of", "json", path], cancellation))["packets"].Objects().FirstOrDefault();
            if (packet is null || packet.Integer("size") > 1024 * 1024)
            {
                return null;
            }

            var message = ProtobufFields(ReadHexDump(packet.Text("data")));
            var header = ProtobufFields((byte[])ProtobufFields((byte[])message[1])[1]);
            // The same field number can mean something else in another DJI
            // schema. Unknown schemas fall back to generic stream/tag detection.
            var schema = Encoding.ASCII.GetString((byte[])header[1]);
            if (schema is not ("dvtm_PP-101.proto" or "dvtm_ac206.proto" or "dvtm_oq101.proto"))
            {
                return null;
            }

            var color = ProtobufFields((byte[])ProtobufFields((byte[])message[2])[4]);
            return (schema, color.TryGetValue(1, out var mode) ? (ulong)mode : 0);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or KeyNotFoundException or InvalidCastException or OverflowException or TimeoutException) { return null; }
    }

    /// <summary>Reads H.264 SPS chroma_format_idc when decoder output appears to be YUV420 despite a gray bitstream.</summary>
    /// <remarks>Supports AVC configuration records and Annex B. Invalid extradata falls back to normal pixel descriptors.</remarks>
    public static bool AvcMonochrome(JsonObject video)
    {
        if (video.Text("codec_name") != "h264")
        {
            return false;
        }

        try
        {
            var data = ReadHexDump(video.Text("extradata"));
            if (data.Length > 0 && data[0] == 1)
            {
                if (data.Length < 8 || (data[5] & 31) == 0)
                {
                    return false;
                }

                data = data.AsSpan(8, (data[6] << 8) | data[7]).ToArray();
            }
            else
            {
                int start = -1, end = data.Length;
                for (int i = 0; i + 3 < data.Length; i++)
                {
                    if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || data[i + 2] == 0 && data[i + 3] == 1))
                    {
                        int offset = i + (data[i + 2] == 1 ? 3 : 4);
                        if (start >= 0)
                        {
                            end = i;
                            break;
                        }
                        if (offset < data.Length && (data[offset] & 31) == 7)
                        {
                            start = offset;
                        }

                        i = offset - 1;
                    }
                }

                if (start < 0)
                {
                    return false;
                }

                data = data[start..end];
            }
            if (data.Length < 4 || (data[0] & 31) != 7)
            {
                return false;
            }

            var rbsp = new List<byte>();
            for (int i = 1; i < data.Length; i++)
            {
                if (!(i >= 3 && data[i] == 3 && data[i - 1] == 0 && data[i - 2] == 0))
                {
                    rbsp.Add(data[i]);
                }
            }

            if (!new[] { 44, 83, 86, 100, 110, 118, 122, 128, 134, 135, 138, 139, 144, 244 }.Contains((int)rbsp[0]))
            {
                return false;
            }
            // Skip profile/constraint/level bytes, then the unsigned Exp-Golomb
            // seq_parameter_set_id. chroma_format_idc == 0 is the next single 1 bit.
            int bit = 24;
            int ReadBit()
            {
                if (bit >= rbsp.Count * 8)
                {
                    throw new InvalidDataException();
                }

                return (rbsp[bit / 8] >> (7 - bit++ % 8)) & 1;
            }
            int zeros = 0;
            while (ReadBit() == 0)
            {
                if (++zeros > 31)
                {
                    return false;
                }
            }
            bit += zeros;
            return ReadBit() == 1;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidDataException or IndexOutOfRangeException) { return false; }
    }

    /// <summary>Creates a detached presentation snapshot; detailed mode also includes original probe evidence.</summary>
    public static JsonObject Describe(MediaInfo media, bool detailed = false)
    {
        JsonObject Select(JsonObject source, string[] fields)
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
