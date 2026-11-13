using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

public static class OutputPolicy
{
    private static readonly Dictionary<string, (int W, int H)> shifts = new()
        { ["444"] = (0, 0), ["422"] = (1, 0), ["420"] = (1, 1), ["440"] = (0, 1), ["411"] = (2, 0), ["410"] = (2, 2) };
    public static bool IsRgb(MediaInfo m) => m.Pixel["flags"].B("rgb") || m.Pixel["flags"].B("palette")
        || new[] { "rgb", "bgr", "gbr", "argb", "abgr", "rgba", "bgra" }.Any(p => m.Video.S("pix_fmt").StartsWith(p)) || m.Video.S("pix_fmt") == "pal8";
    public static string SourceSampling(MediaInfo m)
    {
        if (m.Video.B("_monochrome")) return "gray";
        if (m.Pixel["flags"].B("bayer")) return "unknown";
        if (IsRgb(m)) return "444";
        var name = m.Video.S("pix_fmt");
        if (m.Pixel.I("nb_components") is 1 or 2 || name.StartsWith("gray") || name.StartsWith("ya")) return "gray";
        if (m.Pixel.ContainsKey("log2_chroma_w") && m.Pixel.ContainsKey("log2_chroma_h"))
            return shifts.FirstOrDefault(p => p.Value == (m.Pixel.I("log2_chroma_w"), m.Pixel.I("log2_chroma_h"))).Key ?? "unknown";
        var match = Regex.Match(name, "(420|422|444|440|411|410)");
        return match.Success ? match.Value : name switch { "nv12" or "nv21" or "p010le" or "p012le" or "p016le" => "420", "yuyv422" or "uyvy422" => "422", _ => "unknown" };
    }
    public static int SourceDepth(MediaInfo m)
    {
        var components = m.Pixel["components"].Objects().ToArray();
        if (m.Pixel["flags"].B("alpha") && components.Length > 1) components = components[..^1];
        var depths = components.Select(c => c.I("bit_depth")).Where(d => d > 0).ToArray();
        var raw = m.Video.I("bits_per_raw_sample"); if (raw <= 0) raw = m.Video.I("bits_per_sample");
        if (depths.Length > 0) return raw > 0 ? Math.Min(depths.Max(), raw) : depths.Max();
        if (raw > 0) return raw;
        var match = Regex.Match(m.Video.S("pix_fmt"), "(?:p|gray)(10|12|14|16)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 8;
    }
    public static string[] SamplingChoices(MediaInfo m)
    {
        var sampling = SourceSampling(m);
        if (sampling == "gray") return ["gray", "420"];
        if (!shifts.TryGetValue(sampling, out var source)) return [];
        return new[] { "420", "422", "444" }.Where(n => shifts[n].W >= source.W && shifts[n].H >= source.H).ToArray();
    }
    public static int[] DepthChoices(MediaInfo m, string purpose, string codec, string output)
    {
        int[] supported = purpose == "share" && codec == "avc" ? [8] : purpose == "archive" && codec == "hevc" ? [8, 10, 12] : [8, 10];
        return supported.Where(d => d <= Math.Max(8, SourceDepth(m)) && (output != "hdr" || d >= 10)).ToArray();
    }
    public static int? DefaultDepth(MediaInfo m, string purpose, string codec, string output)
    {
        var choices = DepthChoices(m, purpose, codec, output);
        return choices.Length == 0 ? null : purpose == "archive" ? choices.Max() : output == "hdr" ? 10 : 8;
    }
    public static string DefaultSampling(MediaInfo m, string purpose, string codec)
    {
        if (purpose == "share" || codec == "av1") return "420";
        if (IsRgb(m)) return "444";
        var sampling = SourceSampling(m);
        if (sampling == "440") return "420";
        if (m.Pixel["flags"].B("alpha")) return sampling is "420" or "422" or "444" or "gray" ? sampling : "420";
        return "source";
    }
    public static string SourceRange(MediaInfo m, string? profileRange = null)
    {
        var range = m.Video.S("color_range");
        if (range is "pc" or "full" or "jpeg") return "full";
        if (range is "tv" or "limited" or "mpeg") return "limited";
        if (IsRgb(m) || m.Video.S("pix_fmt").StartsWith("yuvj")) return "full";
        return profileRange ?? (m.Profile == "milog" ? "full" : "limited");
    }
    public static (JsonObject Stream, int Depth)[] FlacTracks(MediaInfo m)
    {
        var streams = m.Audio.ToArray();
        if (streams.Length == 0) throw new InvalidDataException("FLAC 需要素材包含音轨。");
        string[] lossless = ["flac", "alac", "ape", "tta", "tak", "shorten", "truehd", "mlp", "wmalossless"];
        string[] layouts = ["", "mono", "stereo", "3.0", "quad", "5.0", "5.1", "6.1", "7.1"];
        return streams.Select((stream, index) =>
        {
            var codec = stream.S("codec_name");
            var pcm = Regex.Match(codec, @"\Apcm_[su](\d+)(?:le|be)?(?:_planar)?\z");
            if (!(lossless.Contains(codec) || pcm.Success || codec == "dts" && stream.S("profile") == "DTS-HD MA")
                || stream.S("sample_fmt").StartsWith("flt") || stream.S("sample_fmt").StartsWith("dbl"))
                throw new InvalidDataException($"音轨 {index + 1}（{codec}）无法确认为 FLAC 支持的整数无损输入，请选择复制、AAC 或 Opus。");
            int depth = stream.I("bits_per_raw_sample");
            if (depth <= 0) depth = stream.I("bits_per_sample");
            if (depth <= 0 && pcm.Success) depth = int.Parse(pcm.Groups[1].Value);
            int channels = stream.I("channels");
            if (depth is < 1 or > 32 || channels is < 1 or > 8)
                throw new InvalidDataException($"音轨 {index + 1} 需要明确的 1–32 位整数位深及 1–8 声道，才能转为 FLAC。");
            var layout = stream.S("channel_layout");
            if (!JsonFields.Unspecified(layout) && layout != layouts[channels] || channels > 2 && JsonFields.Unspecified(layout))
                throw new InvalidDataException($"音轨 {index + 1} 的声道布局无法原样写入 FLAC，请选择复制原音轨。");
            return (stream, depth);
        }).ToArray();
    }
    public static (bool Eligible, string Reason) FlacEligibility(MediaInfo m)
    {
        try { FlacTracks(m); return (true, ""); }
        catch (InvalidDataException e) { return (false, e.Message); }
    }
}
