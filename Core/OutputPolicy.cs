using System.Text.Json.Nodes;
using LumaRecode.Native;

namespace VideoConverter.Core;

/// <summary>Presentation adapter for source precision and choices owned by the shared core.</summary>
public static class OutputPolicy
{
    private static SourceFormat Source(MediaInfo media, string? profileRange = null)
    {
        var status = NativeMedia.ReadSourceFormat(NativeBackend.Snapshot(media, formatOnly: true), out var source,
            out string diagnostic, profileRange == "full" ? ColorRange.Full : profileRange == "limited" ? ColorRange.Limited : ColorRange.Auto);
        NativeBackend.Check(status, diagnostic);
        return source;
    }
    private static FormatChoices Choices(SourceFormat source, string purpose, string codec, string output)
    {
        var status = NativeCore.GetFormatChoices(source, purpose == "share" ? Purpose.Share : Purpose.Archive,
            codec == "hevc" ? Codec.Hevc : codec == "avc" ? Codec.Avc : Codec.Av1,
            output == "hdr" ? DynamicRange.Hdr : DynamicRange.Sdr, out var choices, out string diagnostic);
        NativeBackend.Check(status, diagnostic);
        return choices;
    }
    internal static string SamplingName(Sampling sampling) => sampling switch
    {
        Sampling.Yuv420 => "420",
        Sampling.Yuv422 => "422",
        Sampling.Yuv444 or Sampling.Rgb => "444",
        Sampling.Gray => "gray",
        Sampling.Yuv440 => "440",
        Sampling.Yuv411 => "411",
        Sampling.Yuv410 => "410",
        Sampling.Source => "source",
        _ => "unknown"
    };
    public static bool IsRgb(MediaInfo media) => Source(media).Flags.HasFlag(SourceFlags.Rgb);
    public static string SourceSampling(MediaInfo media) => SamplingName(Source(media).Sampling);
    public static int SourceDepth(MediaInfo media) => checked((int)Source(media).BitDepth);
    public static string SourceRange(MediaInfo media, string? profileRange = null)
        => Source(media, profileRange).ColorRange == ColorRange.Full ? "full" : "limited";
    public static string[] SamplingChoices(MediaInfo media)
    {
        var choices = Choices(Source(media), "archive", "hevc", "sdr");
        return new[] { Sampling.Gray, Sampling.Yuv420, Sampling.Yuv422, Sampling.Yuv444 }
            .Where(value => (choices.SamplingMask & (1U << (int)value)) != 0).Select(SamplingName).ToArray();
    }
    public static int[] DepthChoices(MediaInfo media, string purpose, string codec, string output)
        => Depths(Choices(Source(media), purpose, codec, output));
    public static int[] DepthChoices(int sourceDepth, string purpose, string codec, string output)
    {
        var source = NativeCore.CreateSourceFormat();
        source.BitDepth = (uint)Math.Clamp(sourceDepth, 1, 64);
        source.Sampling = Sampling.Yuv420;
        return Depths(Choices(source, purpose, codec, output));
    }
    private static int[] Depths(FormatChoices choices) => new[] { 8, 10, 12 }
        .Where(value => (choices.DepthMask & (1U << value)) != 0).ToArray();
    public static int? DefaultDepth(MediaInfo media, string purpose, string codec, string output)
    {
        uint value = Choices(Source(media), purpose, codec, output).DefaultDepth;
        return value == 0 ? null : (int)value;
    }
    public static string DefaultSampling(MediaInfo media, string purpose, string codec)
        => SamplingName(Choices(Source(media), purpose, codec, "sdr").DefaultSampling);
    public static (JsonObject Stream, int Depth)[] FlacTracks(MediaInfo media)
    {
        var tracks = media.Audio.ToArray();
        if (tracks.Length == 0)
        {
            throw new InvalidDataException("FLAC 需要素材包含音轨。");
        }

        return tracks.Select((stream, index) =>
        {
            var status = NativeMedia.FlacDepth(NativeBackend.JsonBytes(stream), out uint depth, out string diagnostic);
            // Keep host-facing translation templates while the native owner
            // decides eligibility. The track number belongs to this host list.
            diagnostic = diagnostic switch
            {
                "FLAC requires lossless integer audio." => $"音轨 {index + 1}（{stream.Text("codec_name")}）无法确认为 FLAC 支持的整数无损输入，请选择复制、AAC 或 Opus。",
                "FLAC requires known 1-32 bit precision and 1-8 channels." => $"音轨 {index + 1} 需要明确的 1–32 位整数位深及 1–8 声道，才能转为 FLAC。",
                "Audio channel order cannot be preserved in FLAC." => $"音轨 {index + 1} 的声道布局无法原样写入 FLAC，请选择复制原音轨。",
                _ => diagnostic
            };
            NativeBackend.Check(status, diagnostic);
            return (stream, (int)depth);
        }).ToArray();
    }
    public static (bool Eligible, string Reason) FlacEligibility(MediaInfo media)
    {
        try
        {
            FlacTracks(media);
            return (true, "");
        }
        catch (InvalidDataException error) { return (false, error.Message); }
    }
}
