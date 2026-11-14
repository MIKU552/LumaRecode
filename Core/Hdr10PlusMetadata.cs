using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

/// <summary>Streams ffprobe's presentation-ordered HDR10+ side data into x265 JSON.</summary>
/// <remarks>Numeric units follow FFmpeg's AVDynamicHDRPlus API. The JSON field
/// names are the x265 LLC input schema; no external metadata utility is needed.</remarks>
public static class Hdr10PlusMetadata
{
    private const string TypeName = "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)";
    private static readonly HashSet<string> fields = ["application version", "num_windows", "targeted_system_display_maximum_luminance",
        "maxscl", "average_maxrgb", "num_distribution_maxrgb_percentiles", "distribution_maxrgb_percentage",
        "distribution_maxrgb_percentile", "fraction_bright_pixels", "knee_point_x", "knee_point_y", "num_bezier_curve_anchors", "bezier_curve_anchors"];

    private static InvalidDataException Unsupported(string detail) => new($"无法完整保留此 HDR10+ 元数据：{detail}。请选择标准 HDR。");

    private static int Number(string text, int scale, int maximum)
    {
        // Decimal arithmetic preserves exact integer codes when ffprobe uses
        // reduced rational denominators. Never round malformed/off-grid data.
        var parts = text.Split('/');
        if (parts.Length is < 1 or > 2
            || !decimal.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            || (parts.Length == 2 && !decimal.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            throw Unsupported("invalid rational");
        }
        var denominator = parts.Length == 1 ? 1 : decimal.Parse(parts[1], CultureInfo.InvariantCulture);
        if (denominator <= 0 || numerator < 0 || numerator > maximum * denominator / scale)
        {
            throw Unsupported("out-of-range value");
        }
        var value = numerator * scale / denominator;
        if (decimal.Truncate(value) != value)
        {
            throw Unsupported("off-grid value");
        }
        return (int)value;
    }

    private static JsonObject ParseFrame(string line)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool selected = false, found = false;
        foreach (var entry in line.Split('|'))
        {
            int separator = entry.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }
            var key = entry[..separator];
            key = key[(key.LastIndexOf(':') + 1)..];
            var value = entry[(separator + 1)..];
            if (key == "side_data_type")
            {
                selected = value == TypeName;
                if (selected && found)
                {
                    throw Unsupported("multiple HDR10+ records on one frame");
                }
                found |= selected;
            }
            else if (selected)
            {
                if (!fields.Contains(key))
                {
                    // x265's JSON reader cannot round-trip peak-luminance
                    // grids, saturation mapping or extra processing windows.
                    throw Unsupported(key);
                }
                if (!values.TryGetValue(key, out var items))
                {
                    values[key] = items = [];
                }
                items.Add(value);
            }
        }
        if (!found)
        {
            // A missing frame must not shift all subsequent metadata by one
            // index, nor inherit a previous scene without explicit semantics.
            throw Unsupported("frame without HDR10+ metadata");
        }
        int[] Many(string key, int scale, int maximum) => values.TryGetValue(key, out var items)
            ? items.Select(v => Number(v, scale, maximum)).ToArray() : [];
        int One(string key, int scale = 1, int maximum = int.MaxValue)
        {
            var items = Many(key, scale, maximum);
            return items.Length == 1 ? items[0] : throw Unsupported(key);
        }
        if (One("application version") != 1 || One("num_windows") != 1 || One("fraction_bright_pixels", 1000, 1000) != 0)
        {
            throw Unsupported("application version, processing windows or bright-pixel fraction");
        }
        int target = One("targeted_system_display_maximum_luminance", 1, 10000);
        var maxScl = Many("maxscl", 100000, 100000);
        var indices = Many("distribution_maxrgb_percentage", 1, 100);
        var distributions = Many("distribution_maxrgb_percentile", 100000, 100000);
        int count = One("num_distribution_maxrgb_percentiles", 1, 15);
        if (maxScl.Length != 3 || indices.Length != count || distributions.Length != count
            || !indices.SequenceEqual(indices.Distinct().Order()))
        {
            throw Unsupported("luminance distributions");
        }
        var result = new JsonObject
        {
            ["LuminanceParameters"] = new JsonObject
            {
                ["AverageRGB"] = One("average_maxrgb", 100000, 100000),
                ["LuminanceDistributions"] = new JsonObject { ["DistributionIndex"] = Integers(indices), ["DistributionValues"] = Integers(distributions) },
                ["MaxScl"] = Integers(maxScl)
            },
            ["NumberOfWindows"] = 1,
            ["TargetedSystemDisplayMaximumLuminance"] = target
        };
        bool curve = values.ContainsKey("knee_point_x");
        if (curve)
        {
            var anchors = Many("bezier_curve_anchors", 1023, 1023);
            int anchorCount = One("num_bezier_curve_anchors", 1, 15);
            if (target == 0 || anchorCount == 0 || anchors.Length != anchorCount)
            {
                throw Unsupported("Bezier curve");
            }
            result["BezierCurveData"] = new JsonObject
            {
                ["Anchors"] = Integers(anchors),
                ["KneePointX"] = One("knee_point_x", 4095, 4095),
                ["KneePointY"] = One("knee_point_y", 4095, 4095)
            };
        }
        else if (target != 0 || values.Keys.Any(k => k is "knee_point_y" or "num_bezier_curve_anchors" or "bezier_curve_anchors"))
        {
            throw Unsupported("incomplete tone-mapping curve");
        }
        return result;
    }

    private static JsonArray Integers(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>Writes the x265 per-frame schema without retaining the movie's metadata in memory.</summary>
    /// <remarks>Every decoded frame needs one representable HDR10+ record. Unsupported fields
    /// fail before encoding. Scene summaries are omitted because x265 uses SceneInfo directly.</remarks>
    public static async Task ExportAsync(TextReader frames, Stream output, CancellationToken cancellation = default, FrameRange? selection = null)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteStartArray("SceneInfo");
        string? previous = null, profile = null;
        long sequence = 0, scene = -1, sceneStart = 0, inputFrame = 0;
        while (await frames.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("frame|", StringComparison.Ordinal) && line != "frame")
            {
                continue;
            }
            long index = inputFrame++;
            if (index < (selection?.Start ?? 0))
            {
                continue;
            }
            if (selection?.End is { } end && index >= end)
            {
                // Continue draining the probe pipe without retaining or parsing
                // the rest of the movie. Scene indices below start at zero.
                continue;
            }
            if (line.Length > 65536)
            {
                throw Unsupported("oversized frame record");
            }
            var metadata = ParseFrame(line);
            var current = metadata.ToJsonString();
            var frameProfile = metadata.ContainsKey("BezierCurveData") ? "B" : "A";
            if (profile is not null && profile != frameProfile)
            {
                throw Unsupported("mixed HDR10+ profiles");
            }
            profile = frameProfile;
            if (current != previous)
            {
                scene++;
                sceneStart = sequence;
                previous = current;
            }
            metadata["SceneFrameIndex"] = sequence - sceneStart;
            metadata["SceneId"] = scene;
            metadata["SequenceFrameIndex"] = sequence++;
            metadata.WriteTo(writer);
            // The writer's buffer is drained once per frame, bounding managed
            // memory independently of duration and scene-change frequency.
            await writer.FlushAsync(cancellation);
        }
        if (sequence == 0)
        {
            throw new InvalidDataException("素材中没有可保留的 HDR10+ 逐帧元数据。");
        }
        writer.WriteEndArray();
        writer.WriteStartObject("JSONInfo");
        writer.WriteString("HDR10plusProfile", profile);
        writer.WriteString("Version", "1.0");
        writer.WriteEndObject();
        writer.WriteStartObject("ToolInfo");
        writer.WriteString("Tool", "LumaRecode");
        writer.WriteString("Version", typeof(Hdr10PlusMetadata).Assembly.GetName().Version?.ToString(3));
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellation);
    }

    internal static async Task<string> ReadErrorTailAsync(TextReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 4096)
            {
                tail.Remove(0, tail.Length - 4096);
            }
        }
        return tail.ToString().Trim();
    }
}
