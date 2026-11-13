using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

/// <summary>Reads the mixed string/numeric fields emitted by ffprobe without culture-dependent conversion.</summary>
public static class JsonFields
{
    public static string Text(this JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    public static int Integer(this JsonNode? node, string key, int fallback = 0) => int.TryParse(node.Text(key), CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public static bool Flag(this JsonNode? node, string key) => node.Text(key) is "true" or "1";
    public static IEnumerable<JsonObject> Objects(this JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : [];
    public static double Number(string? text, double fallback = 0)
    {
        var pieces = text?.Split('/');
        if (pieces is { Length: 2 })
        {
            // A valid numerator and denominator can still overflow when divided.
            // Keep NaN/Infinity out of progress JSON and encoder metadata alike.
            var numerator = Number(pieces[0], double.NaN);
            var denominator = Number(pieces[1], double.NaN);
            var quotient = numerator / denominator;
            return double.IsFinite(quotient) ? quotient : fallback;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    }
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static bool Unspecified(string? value) => value is null or "" or "unknown" or "unspecified" or "reserved";
}

/// <summary>Resolved executable paths. Arguments are always passed separately through ProcessStartInfo.</summary>
public sealed record MediaTools(string Ffmpeg, string Ffprobe);
/// <summary>A source inspection snapshot, including first-frame side data and pixel-format descriptors.</summary>
/// <remarks>JSON nodes belong to this snapshot; clone a node before attaching it to another JSON parent.</remarks>
public sealed record MediaInfo(string Path, JsonObject Video, JsonArray Frames, JsonObject Tags, string Kind,
    string? Profile, string Evidence, double Duration, JsonArray Streams, JsonObject Pixel)
{
    public JsonObject? Raw => Video["_raw"] as JsonObject;
    public JsonObject? DolbyVision => SideData.FirstOrDefault(s => s.Text("side_data_type") == "DOVI configuration record");
    public bool Hdr10Plus => SideData.Any(s => s.Text("side_data_type").Contains("HDR10+", StringComparison.Ordinal));
    public bool DynamicHdr => DolbyVision is not null || Hdr10Plus;
    public IEnumerable<JsonObject> Audio => Streams.Objects().Where(s => s.Text("codec_type") == "audio");
    public IEnumerable<JsonObject> SideData => Video["side_data_list"].Objects().Concat(Frames.Objects().SelectMany(f => f["side_data_list"].Objects()));
}

/// <summary>Shared GUI/CLI encoding choices. ResolveFormat replaces source-dependent "auto" values before planning.</summary>
/// <remarks>Output is SDR/HDR; ColorRange is the independent Limited/Full sample range.</remarks>
public sealed record EncodeOptions(string Purpose = "share", string Codec = "hevc", string Output = "sdr",
    string Transform = "none", string? Profile = null, string Sampling = "auto", string Variant = "standard",
    string Audio = "auto", string SourceKind = "auto", bool CpuDecode = false, string Container = "auto",
    string ColorRange = "auto", string BitDepth = "auto", string DynamicHdr = "preserve")
{
    public string Suffix => "." + (Container == "auto" ? Purpose == "archive" || Audio == "flac" ? "mkv" : "mp4" : Container);
    public int Depth => int.Parse(BitDepth, CultureInfo.InvariantCulture);
    public string PixelFormat => Sampling == "gray" ? Depth > 8 ? $"gray{Depth}le" : "gray" : $"yuv{Sampling}p" + (Depth > 8 ? $"{Depth}le" : "");
    public JsonObject ToJson() => new()
    {
        ["purpose"] = Purpose,
        ["codec"] = Codec,
        ["output"] = Output,
        ["transform"] = Transform,
        ["profile"] = Profile,
        ["sampling"] = Sampling,
        ["variant"] = Variant,
        ["audio"] = Audio,
        ["source_kind"] = SourceKind,
        ["cpu_decode"] = CpuDecode,
        ["container"] = Container,
        ["color_range"] = ColorRange,
        ["bit_depth"] = BitDepth,
        ["dynamic_hdr"] = DynamicHdr
    };
    public static EncodeOptions FromJson(JsonNode? node) => new(node.Text("purpose", "share"), node.Text("codec", "hevc"), node.Text("output", "sdr"),
        node.Text("transform", "none"), node?["profile"]?.ToString(), node.Text("sampling", "auto"), node.Text("variant", "standard"),
        node.Text("audio", "auto"), node.Text("source_kind", "auto"), node.Flag("cpu_decode"), node.Text("container", "auto"),
        node.Text("color_range", "auto"), node.Text("bit_depth", "auto"), node.Text("dynamic_hdr", "preserve"));
}

/// <summary>A shell-free FFmpeg argument vector and the color tags required during output verification.</summary>
public sealed record EncodingPlan(string[] Arguments, JsonObject Colors, EncodeOptions Options, bool QsvTimingCompatibility = false, string? WorkingDirectory = null);
