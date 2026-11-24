namespace VideoConverter.Core;

/// <summary>Produces viewport-sized SDR frames through one bounded FFmpeg pipe.</summary>
/// <remarks>Only previews resize before color rendering. Exports retain their full-resolution processing path.</remarks>
public sealed class FramePreview(Transcoder engine)
{
    public const int Width = 640;
    public const int Height = 360;
    public const int PixelBytes = Width * Height * 4;
    public const int BatchFrames = 4;
    internal static string ScaleFilter(int maximum) => $"scale=w='min(iw,{maximum})':h='min(ih,{maximum})':force_original_aspect_ratio=decrease:force_divisible_by=2";

    public Task<byte[]> RenderAsync(MediaTools tools, MediaInfo source, FrameTimeline timeline,
        ImageExportOptions options, CancellationToken cancellation = default) =>
        RenderAsync(tools, source, timeline, options, 1, pixels: false, cancellation);

    /// <summary>Decodes a short run of tightly packed BGRA frames for the interactive viewport.</summary>
    public Task<byte[]> RenderPixelsAsync(MediaTools tools, MediaInfo source, FrameTimeline timeline,
        ImageExportOptions options, int count, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, BatchFrames);
        return RenderAsync(tools, source, timeline, options, count, pixels: true, cancellation);
    }

    private async Task<byte[]> RenderAsync(MediaTools tools, MediaInfo source, FrameTimeline timeline,
        ImageExportOptions options, int count, bool pixels, CancellationToken cancellation)
    {
        var window = timeline.Resolve(new(options.Frame, checked(options.Frame + count)));
        var plan = engine.Plan(tools, source, FrameExporter.RenderOptions(source, options, sdr: true),
            "preview.mkv", window: window, previewSize: pixels ? Width : 1024, cpuToneMap: pixels);
        int input = Array.IndexOf(plan.Arguments, "-i");
        var arguments = plan.Arguments.Take(input + 2).Skip(1).ToList();
        // Frame-threaded decoders otherwise decode many 4K frames ahead before
        // delivering the single requested frame. Limit that latency and memory.
        arguments.InsertRange(0, ["-threads:v", "2", "-filter_threads", "2"]);
        string filter = plan.Arguments[Array.IndexOf(plan.Arguments, "-vf") + 1]
            + ",zscale=primaries=bt709:transfer=iec61966-2-1:matrix=gbr:range=full,format=gbrp16le,format=rgb24";
        filter += pixels
            ? FormattableString.Invariant($",scale={Width}:{Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,format=bgra")
            : ",iccgen=color_primaries=bt709:color_trc=iec61966-2-1:force=1";
        arguments.AddRange(["-map", "0:" + source.Video.Text("index"), "-an", "-sn", "-dn", "-map_metadata", "-1", "-vf", filter,
            "-frames:v", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "-fps_mode", "passthrough"]);
        arguments.AddRange(pixels
            ? ["-c:v", "rawvideo", "-threads:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]
            : ["-c:v", "png", "-compression_level", "3", "-f", "image2pipe", "pipe:1"]);
        DirectoryInfo? scratch = null;
        bool cleanupConfirmed = true;
        try
        {
            if (source.Raw is not null)
            {
                scratch = Directory.CreateTempSubdirectory("LumaRecode-preview-");
                await RawDevelopment.PrepareAsync(tools, source, Path.Combine(scratch.FullName, "raw-calibration.cmd"), null, cancellation, window).ConfigureAwait(false);
            }
            var result = await ProcessRunner.CaptureBinaryAsync(tools.Ffmpeg, arguments, cancellation,
                maximumBytes: pixels ? PixelBytes * count : 8 * 1024 * 1024,
                directory: scratch?.FullName ?? engine.Assets).ConfigureAwait(false);
            bool complete = pixels ? result.Output.Length == PixelBytes * count
                : result.Output.AsSpan().StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]);
            if (result.ExitCode != 0 || !complete)
            {
                throw new InvalidDataException("图片提取失败：" + result.Error);
            }
            return result.Output;
        }
        catch (NativeCleanupException)
        {
            cleanupConfirmed = false;
            throw;
        }
        finally
        {
            if (cleanupConfirmed && scratch is not null)
            {
                try
                {
                    File.Delete(Path.Combine(scratch.FullName, "raw-calibration.cmd"));
                    scratch.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
