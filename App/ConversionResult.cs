using VideoConverter.Core;

namespace VideoConverter;

/// <summary>Output facts captured after encoding, verification and publication have succeeded.</summary>
public sealed record ConversionResult(string Path, long Bytes, double Duration, string Format)
{
    internal static ConversionResult FromMedia(string output, MediaInfo media)
    {
        var codec = media.Video.Text("codec_name") switch
        {
            "hevc" => "HEVC / H.265",
            "h264" => "AVC / H.264",
            "av1" => "AV1",
            var name => name.ToUpperInvariant()
        };
        return new(output, new FileInfo(output).Length, media.Duration,
            System.IO.Path.GetExtension(output).TrimStart('.').ToUpperInvariant() + " · " + codec);
    }
}
