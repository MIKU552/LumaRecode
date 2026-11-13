using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoConverter.Core;

public static class JsonFields
{
    public static string S(this JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    public static int I(this JsonNode? node, string key, int fallback = 0) => int.TryParse(node.S(key), CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public static bool B(this JsonNode? node, string key) => node.S(key) is "true" or "1";
    public static IEnumerable<JsonObject> Objects(this JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : [];
    public static double Number(string? text, double fallback = 0)
    {
        var pieces = text?.Split('/');
        if (pieces is { Length: 2 }) return Number(pieces[1]) is var denominator && denominator != 0 ? Number(pieces[0]) / denominator : fallback;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    }
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static bool Unspecified(string? value) => value is null or "" or "unknown" or "unspecified" or "reserved";
}

public sealed record MediaTools(string Ffmpeg, string Ffprobe);
public sealed record MediaInfo(string Path, JsonObject Video, JsonArray Frames, JsonObject Tags, string Kind,
    string? Profile, string Evidence, double Duration, JsonArray Streams, JsonObject Pixel)
{
    public IEnumerable<JsonObject> Audio => Streams.Objects().Where(s => s.S("codec_type") == "audio");
    public IEnumerable<JsonObject> SideData => Video["side_data_list"].Objects().Concat(Frames.Objects().SelectMany(f => f["side_data_list"].Objects()));
}

public sealed record EncodeOptions(string Purpose = "share", string Codec = "hevc", string Output = "sdr",
    string Transform = "none", string? Profile = null, string Sampling = "auto", string Variant = "standard",
    string Audio = "auto", string SourceKind = "auto", bool CpuDecode = false, string Container = "auto",
    string ColorRange = "auto", string BitDepth = "auto")
{
    public string Suffix => "." + (Container == "auto" ? Purpose == "archive" || Audio == "flac" ? "mkv" : "mp4" : Container);
    public int Depth => int.Parse(BitDepth, CultureInfo.InvariantCulture);
    public string PixelFormat => Sampling == "gray" ? Depth > 8 ? $"gray{Depth}le" : "gray" : $"yuv{Sampling}p" + (Depth > 8 ? $"{Depth}le" : "");
    public JsonObject ToJson() => new() { ["purpose"] = Purpose, ["codec"] = Codec, ["output"] = Output,
        ["transform"] = Transform, ["profile"] = Profile, ["sampling"] = Sampling, ["variant"] = Variant,
        ["audio"] = Audio, ["source_kind"] = SourceKind, ["cpu_decode"] = CpuDecode, ["container"] = Container,
        ["color_range"] = ColorRange, ["bit_depth"] = BitDepth };
    public static EncodeOptions FromJson(JsonNode? n) => new(n.S("purpose", "share"), n.S("codec", "hevc"), n.S("output", "sdr"),
        n.S("transform", "none"), n?["profile"]?.ToString(), n.S("sampling", "auto"), n.S("variant", "standard"),
        n.S("audio", "auto"), n.S("source_kind", "auto"), n.B("cpu_decode"), n.S("container", "auto"),
        n.S("color_range", "auto"), n.S("bit_depth", "auto"));
}

public sealed record EncodingPlan(string[] Arguments, JsonObject Colors, EncodeOptions Options);
