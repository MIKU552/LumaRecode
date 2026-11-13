using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

/// <summary>Source-aware format limits shared by GUI defaults, CLI planning and output verification.</summary>
public static class OutputPolicy
{
    // Chroma shifts are horizontal/vertical powers of two. Comparing both
    // axes avoids treating 4:4:0 as if it could preserve detail in 4:2:2.
    private static readonly Dictionary<string, (int W, int H)> shifts = new()
    {
        ["444"] = (0, 0),
        ["422"] = (1, 0),
        ["420"] = (1, 1),
        ["440"] = (0, 1),
        ["411"] = (2, 0),
        ["410"] = (2, 2)
    };
    public static bool IsRgb(MediaInfo media) => media.Pixel["flags"].Flag("rgb") || media.Pixel["flags"].Flag("palette")
        || new[] { "rgb", "bgr", "gbr", "argb", "abgr", "rgba", "bgra" }.Any(p => media.Video.Text("pix_fmt").StartsWith(p)) || media.Video.Text("pix_fmt") == "pal8";
    public static string SourceSampling(MediaInfo media)
    {
        if (media.Video.Flag("_monochrome"))
        {
            return "gray";
        }

        if (media.Pixel["flags"].Flag("bayer"))
        {
            return "unknown";
        }

        if (IsRgb(media))
        {
            return "444";
        }

        var name = media.Video.Text("pix_fmt");
        if (media.Pixel.Integer("nb_components") is 1 or 2 || name.StartsWith("gray") || name.StartsWith("ya"))
        {
            return "gray";
        }

        if (media.Pixel.ContainsKey("log2_chroma_w") && media.Pixel.ContainsKey("log2_chroma_h"))
        {
            return shifts.FirstOrDefault(p => p.Value == (media.Pixel.Integer("log2_chroma_w"), media.Pixel.Integer("log2_chroma_h"))).Key ?? "unknown";
        }

        var match = Regex.Match(name, "(420|422|444|440|411|410)");
        return match.Success ? match.Value : name switch
        {
            "nv12" or "nv21" or "p010le" or "p012le" or "p016le" => "420",
            "yuyv422" or "uyvy422" => "422",
            _ => "unknown"
        };
    }
    /// <summary>Returns usable color precision, excluding alpha and unused storage bits.</summary>
    public static int SourceDepth(MediaInfo media)
    {
        var components = media.Pixel["components"].Objects().ToArray();
        if (media.Pixel["flags"].Flag("alpha") && components.Length > 1)
        {
            components = components[..^1];
        }

        var depths = components.Select(c => c.Integer("bit_depth")).Where(d => d > 0).ToArray();
        var raw = media.Video.Integer("bits_per_raw_sample");
        if (raw <= 0)
        {
            raw = media.Video.Integer("bits_per_sample");
        }

        if (depths.Length > 0)
        {
            return raw > 0 ? Math.Min(depths.Max(), raw) : depths.Max();
        }

        if (raw > 0)
        {
            return raw;
        }

        var match = Regex.Match(media.Video.Text("pix_fmt"), "(?:p|gray)(10|12|14|16)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 8;
    }
    /// <summary>Returns formats with no increase in chroma resolution on either axis.</summary>
    /// <remarks>Gray may use 4:2:0 with neutral chroma; RGB is converted to YCbCr at full chroma resolution.</remarks>
    public static string[] SamplingChoices(MediaInfo media)
    {
        var sampling = SourceSampling(media);
        if (sampling == "gray")
        {
            return ["gray", "420"];
        }

        if (!shifts.TryGetValue(sampling, out var source))
        {
            return [];
        }

        return new[] { "420", "422", "444" }.Where(n => shifts[n].W >= source.W && shifts[n].H >= source.H).ToArray();
    }
    /// <summary>Intersects source precision with encoder support and HDR's 10-bit minimum.</summary>
    public static int[] DepthChoices(MediaInfo media, string purpose, string codec, string output)
        => DepthChoices(SourceDepth(media), purpose, codec, output);
    public static int[] DepthChoices(int sourceDepth, string purpose, string codec, string output)
    {
        int[] supported = purpose == "share" && codec == "avc" ? [8] : purpose == "archive" && codec == "hevc" ? [8, 10, 12] : [8, 10];
        return supported.Where(d => d <= Math.Max(8, sourceDepth) && (output != "hdr" || d >= 10)).ToArray();
    }
    public static int? DefaultDepth(MediaInfo media, string purpose, string codec, string output)
    {
        var choices = DepthChoices(media, purpose, codec, output);
        return choices.Length == 0 ? null : purpose == "archive" ? choices.Max() : output == "hdr" ? 10 : 8;
    }
    public static string DefaultSampling(MediaInfo media, string purpose, string codec)
    {
        if (purpose == "share" || codec == "av1")
        {
            return "420";
        }

        if (IsRgb(media))
        {
            return "444";
        }

        var sampling = SourceSampling(media);
        if (sampling == "440")
        {
            return "420";
        }

        if (media.Pixel["flags"].Flag("alpha"))
        {
            return sampling is "420" or "422" or "444" or "gray" ? sampling : "420";
        }

        return "source";
    }
    /// <summary>Uses explicit range tags first, then pixel-format and camera-profile defaults.</summary>
    /// <remarks>Sample range is independent of SDR/HDR transfer functions.</remarks>
    public static string SourceRange(MediaInfo media, string? profileRange = null)
    {
        var range = media.Video.Text("color_range");
        if (range is "pc" or "full" or "jpeg")
        {
            return "full";
        }

        if (range is "tv" or "limited" or "mpeg")
        {
            return "limited";
        }

        if (IsRgb(media) || media.Video.Text("pix_fmt").StartsWith("yuvj"))
        {
            return "full";
        }

        return profileRange ?? (media.Profile == "milog" ? "full" : "limited");
    }
    /// <summary>Validates every audio track for lossless integer conversion to FLAC.</summary>
    /// <remarks>Each track must have known precision and a FLAC-compatible channel order.
    /// Rejecting floating-point/lossy inputs avoids presenting the conversion as lossless.</remarks>
    public static (JsonObject Stream, int Depth)[] FlacTracks(MediaInfo media)
    {
        var streams = media.Audio.ToArray();
        if (streams.Length == 0)
        {
            throw new InvalidDataException("FLAC 需要素材包含音轨。");
        }

        string[] lossless = ["flac", "alac", "ape", "tta", "tak", "shorten", "truehd", "mlp", "wmalossless"];
        string[] layouts = ["", "mono", "stereo", "3.0", "quad", "5.0", "5.1", "6.1", "7.1"];
        return streams.Select((stream, index) =>
        {
            var codec = stream.Text("codec_name");
            var pcm = Regex.Match(codec, @"\Apcm_[su](\d+)(?:le|be)?(?:_planar)?\z");
            if (!(lossless.Contains(codec) || pcm.Success || codec == "dts" && stream.Text("profile") == "DTS-HD MA")
                || stream.Text("sample_fmt").StartsWith("flt") || stream.Text("sample_fmt").StartsWith("dbl"))
            {
                throw new InvalidDataException($"音轨 {index + 1}（{codec}）无法确认为 FLAC 支持的整数无损输入，请选择复制、AAC 或 Opus。");
            }

            int depth = stream.Integer("bits_per_raw_sample");
            if (depth <= 0)
            {
                depth = stream.Integer("bits_per_sample");
            }

            if (depth <= 0 && pcm.Success)
            {
                depth = int.Parse(pcm.Groups[1].Value);
            }

            int channels = stream.Integer("channels");
            if (depth is < 1 or > 32 || channels is < 1 or > 8)
            {
                throw new InvalidDataException($"音轨 {index + 1} 需要明确的 1–32 位整数位深及 1–8 声道，才能转为 FLAC。");
            }

            var layout = stream.Text("channel_layout");
            if (!JsonFields.Unspecified(layout) && layout != layouts[channels] || channels > 2 && JsonFields.Unspecified(layout))
            {
                throw new InvalidDataException($"音轨 {index + 1} 的声道布局无法原样写入 FLAC，请选择复制原音轨。");
            }

            return (stream, depth);
        }).ToArray();
    }
    public static (bool Eligible, string Reason) FlacEligibility(MediaInfo media)
    {
        try
        {
            FlacTracks(media);
            return (true, "");
        }
        catch (InvalidDataException e) { return (false, e.Message); }
    }
}
