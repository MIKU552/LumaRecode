using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoConverter.Core;

public sealed record HardwareEncoder(string Backend, string Label, string Codec, string Name, int Depth, string Status)
{
    public bool Available => Status == "available";
}

/// <summary>Checks the installed tool and driver with the encoder options used by actual jobs.</summary>
public static partial class HardwareEncoders
{
    [GeneratedRegex(@"^\s*V[A-Z.]{5}\s+(\S+)\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EncoderLine();

    public static HardwareEncoder? Select(IEnumerable<HardwareEncoder> encoders, IEnumerable<string> codecs,
        Func<string, int[]> allowedDepths, string backend = "auto") =>
        codecs.SelectMany(codec => allowedDepths(codec).SelectMany(depth => encoders.Where(item =>
            item.Available && item.Codec == codec && item.Depth == depth && (backend == "auto" || item.Backend == backend))))
        .FirstOrDefault();

    public static async Task<HardwareEncoder[]> ProbeAsync(Transcoder engine, MediaTools tools, CancellationToken cancellation,
        string? backend = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var listing = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"], cancellation,
            timeoutSeconds: 8, maximumBytes: 1024 * 1024).ConfigureAwait(false);
        if (listing.ExitCode != 0)
        {
            return [];
        }

        var listed = EncoderLine().Matches(listing.Output).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var results = new List<HardwareEncoder>();
        // Run trials sequentially so the probe itself does not compete for encoder sessions.
        foreach (var candidate in Candidates(engine.Config).Where(item => backend is null || item.Backend == backend))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!listed.Contains(candidate.Name))
            {
                results.Add(candidate with
                {
                    Status = "not_listed"
                });
                continue;
            }
            results.Add(candidate with
            {
                Status = await TryEncodeAsync(engine, tools, candidate, cancellation).ConfigureAwait(false)
            });
        }
        return [.. results];
    }

    private static IEnumerable<HardwareEncoder> Candidates(JsonObject config)
    {
        foreach (var codec in new[] { "hevc", "av1", "avc" })
        {
            var name = config["share"]![codec].Text("encoder");
            yield return new("qsv", "Intel QSV", codec, name, 8, "");
            if (codec != "avc")
            {
                yield return new("qsv", "Intel QSV", codec, name, 10, "");
            }
        }
        foreach (var (backend, entry) in WindowsPresets(config))
        {
            foreach (var (codec, preset) in entry!["codecs"]!.AsObject())
            {
                foreach (var (depth, _) in preset!["arguments"]!.AsObject())
                {
                    yield return new(backend, entry.Text("label"), codec, preset.Text("encoder"), int.Parse(depth, CultureInfo.InvariantCulture), "");
                }
            }
        }
    }

    internal static IEnumerable<KeyValuePair<string, JsonNode?>> WindowsPresets(JsonObject config) =>
        config["hardware"]!.AsObject().Where(item => item.Value.Text("platform", "windows") == "windows");

    private static async Task<string> TryEncodeAsync(Transcoder engine, MediaTools tools, HardwareEncoder candidate,
        CancellationToken cancellation)
    {
        bool hdr = candidate.Depth == 10;
        string range = hdr ? "hdr" : "sdr";
        string pixel = hdr ? "yuv420p10le" : "yuv420p";
        string path = Path.Combine(engine.Assets, "capability-probe.mp4");
        var video = new JsonObject
        {
            ["index"] = 0,
            ["pix_fmt"] = pixel,
            ["width"] = 320,
            ["height"] = 192,
            ["avg_frame_rate"] = "30/1",
            ["color_range"] = "tv",
            ["color_space"] = hdr ? "bt2020nc" : "bt709",
            ["color_primaries"] = hdr ? "bt2020" : "bt709",
            ["color_transfer"] = hdr ? "arib-std-b67" : "bt709"
        };
        var synthetic = new MediaInfo(path, video, [], [], hdr ? "hlg" : "sdr", null, "", 1, [], []);
        var options = new EncodeOptions(Codec: candidate.Codec, Output: range, CpuDecode: true,
            BitDepth: candidate.Depth.ToString(CultureInfo.InvariantCulture), Hardware: candidate.Backend);
        var plan = engine.Plan(tools, synthetic, options, path);
        var start = Array.IndexOf(plan.Arguments, "-vf");
        var end = Array.IndexOf(plan.Arguments, "-fps_mode:v");
        string[] tag = candidate.Codec == "hevc" ? ["-tag:v", "hvc1"] : [];
        // Exercise the MP4 header as well as the encoder. Fragmented output can
        // be discarded through the Windows null device without a probe file.
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-nostats", "-progress", "pipe:1",
            "-f", "lavfi", "-i", $"testsrc2=size=320x192:rate=30,format={pixel}",
            .. plan.Arguments[start..end], "-an", "-frames:v", "8", .. tag,
            "-movflags", "+frag_keyframe+empty_moov+write_colr", "-f", "mp4", "-y", "NUL"
        ];
        try
        {
            var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, arguments, cancellation,
                timeoutSeconds: 5, maximumBytes: 64 * 1024).ConfigureAwait(false);
            if (candidate.Backend == "qsv" && result.ExitCode != 0 &&
                result.Error.Contains("Low power mode is unsupported", StringComparison.OrdinalIgnoreCase))
            {
                arguments[Array.IndexOf(arguments, "-low_power") + 1] = "-1";
                result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, arguments, cancellation,
                    timeoutSeconds: 5, maximumBytes: 64 * 1024).ConfigureAwait(false);
            }
            return result.ExitCode == 0 && result.Output.Split('\n').Any(line =>
                line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out int frames) && frames > 0)
                ? "available" : "trial_failed";
        }
        catch (TimeoutException)
        {
            return "timed_out";
        }
    }
}
