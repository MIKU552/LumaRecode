// Reference behavior from the pre-migration core; compiled only by differential checks.
using System.Diagnostics;
using System.Globalization;
using VideoConverter.Core;

namespace LumaRecode.Tests.Reference;

/// <summary>Indexes display frames lazily; common progressive codecs use compressed packet timestamps.</summary>
/// <remarks>Discarded MP4 preroll is excluded. Missing/duplicate packet PTS, interlaced
/// streams and other codecs use decoded frame order instead of guessing from FPS.</remarks>
public sealed class ReferenceFrameTimeline
{
    public IReadOnlyList<FramePoint> Frames
    {
        get;
    }
    public long TimeBaseNumerator
    {
        get;
    }
    public long TimeBaseDenominator
    {
        get;
    }
    public bool PacketIndexed
    {
        get;
    }
    private string? sourcePath;
    private long sourceBytes;
    private DateTime sourceModified;
    public int Count => Frames.Count;
    public double Duration => BoundarySeconds(Count) - Seconds(0);
    public bool Matches(string path)
    {
        var file = new FileInfo(path);
        return sourcePath is not null && Path.GetFullPath(path).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            && file.Exists && file.Length == sourceBytes && file.LastWriteTimeUtc == sourceModified;
    }

    private static ReferenceFrameTimeline Capture(ReferenceFrameTimeline timeline, string path)
    {
        var file = new FileInfo(path);
        timeline.sourcePath = file.FullName;
        timeline.sourceBytes = file.Length;
        timeline.sourceModified = file.LastWriteTimeUtc;
        return timeline;
    }

    public ReferenceFrameTimeline(IReadOnlyList<FramePoint> frames, long numerator, long denominator, bool packetIndexed)
        : this(numerator, denominator, packetIndexed, BoundedMediaIndex<FramePoint>.CopySnapshot(frames)) { }

    private ReferenceFrameTimeline(long numerator, long denominator, bool packetIndexed, FramePoint[] frames)
    {
        if (frames.Length == 0 || numerator <= 0 || denominator <= 0)
        {
            throw new InvalidDataException("无法建立视频帧时间轴。");
        }
        // Public inputs are copied once; the loader already owns its bounded
        // snapshot. Avoid another full movie-sized array during publication.
        Frames = Array.AsReadOnly(frames);
        TimeBaseNumerator = numerator;
        TimeBaseDenominator = denominator;
        PacketIndexed = packetIndexed;
    }

    public double Seconds(int frame) => (double)Frames[frame].Pts * TimeBaseNumerator / TimeBaseDenominator;
    public double Position(int frame) => Seconds(frame) - Seconds(0);
    public double BoundarySeconds(int frame)
    {
        if (frame < Count)
        {
            return Seconds(frame);
        }
        var last = Frames[^1];
        long duration = last.Duration > 0 ? last.Duration : Count > 1 ? Math.Max(1, last.Pts - Frames[^2].Pts) : 1;
        return ((double)last.Pts + duration) * TimeBaseNumerator / TimeBaseDenominator;
    }

    /// <summary>Resolves typed time to the first frame at or after that display position.</summary>
    public int AtOrAfter(double position)
    {
        int lower = 0, upper = Count;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (Position(middle) + 1e-9 < position)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return lower;
    }

    public FrameWindow Resolve(FrameRange range)
    {
        int end = range.End ?? Count;
        if (range.Start < 0 || end <= range.Start || end > Count)
        {
            throw new InvalidDataException($"帧范围无效：开始帧须小于结束帧，结束帧不能超过 {Count}。");
        }
        int seek = 0;
        if (PacketIndexed)
        {
            for (int i = range.Start; i >= 0; i--)
            {
                if (Frames[i].KeyFrame && Seconds(i) >= 0)
                {
                    seek = i;
                    break;
                }
            }
        }
        return new(range.Start, end, seek, seek == 0 ? 0 : Seconds(seek), Seconds(range.Start), BoundarySeconds(end));
    }

    public static async Task<ReferenceFrameTimeline> LoadAsync(MediaTools tools, MediaInfo media, CancellationToken cancellation = default)
    {
        var timeBase = media.Video.Text("time_base").Split('/');
        if (timeBase.Length != 2 || !long.TryParse(timeBase[0], out var numerator) || !long.TryParse(timeBase[1], out var denominator) || numerator <= 0 || denominator <= 0)
        {
            throw new InvalidDataException("无法读取视频帧时间基。");
        }
        bool progressive = !new[] { "tt", "bb", "tb", "bt" }.Contains(media.Video.Text("field_order"))
            && !media.Frames.Objects().Any(f => f.Integer("interlaced_frame") == 1);
        if (progressive && media.Video.Text("codec_name") is "h264" or "hevc" or "prores" or "prores_raw" or "mjpeg" or "dnxhd")
        {
            var packets = await ReadAsync(tools, media, packets: true, cancellation).ConfigureAwait(false);
            if (packets is not null && packets.Count > 0)
            {
                packets.Sort((a, b) => a.Pts.CompareTo(b.Pts));
                bool unique = true;
                for (int i = 1; i < packets.Count; i++)
                {
                    if (packets[i - 1].Pts == packets[i].Pts)
                    {
                        unique = false;
                        break;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (unique)
                {
                    return Capture(new(numerator, denominator, true, packets.Snapshot()), media.Path);
                }
            }
        }
        var frames = await ReadAsync(tools, media, packets: false, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return Capture(new(numerator, denominator, false,
            (frames ?? throw new InvalidDataException("无法读取逐帧显示时间。")).Snapshot()), media.Path);
    }

    private static async Task<BoundedMediaIndex<FramePoint>?> ReadAsync(MediaTools tools, MediaInfo media, bool packets, CancellationToken cancellation)
    {
        var arguments = new[] { "-v", "error", "-threads", "2", "-select_streams", media.Video.Text("index"), packets ? "-show_packets" : "-show_frames",
            "-show_entries", packets ? "packet=pts,duration,flags:packet_side_data=" : "frame=best_effort_timestamp,duration,key_frame:frame_side_data=",
            "-of", "compact=p=1:nk=0", media.Path };
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(tools.Ffprobe, arguments) };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellation.Register(() => ProcessRunner.Stop(process));
        var errors = ProcessRunner.ReadTailAsync(process.StandardError, 4096);
        var result = new BoundedMediaIndex<FramePoint>();
        var lines = new BoundedLineReader(process.StandardOutput);
        try
        {
            while (await lines.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith(packets ? "packet|" : "frame|", StringComparison.Ordinal))
                {
                    continue;
                }
                bool valid = TryReadPoint(line, packets, out var point, out bool discarded);
                if (discarded)
                {
                    continue;
                }
                if (!valid)
                {
                    // One missing timestamp already makes this index unusable.
                    // Stop this pass before starting decoded-frame fallback.
                    return null;
                }
                result.Add(point);
            }
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("无法建立视频帧时间轴：" + await errors.ConfigureAwait(false));
            }
            return result;
        }
        finally
        {
            ProcessRunner.Stop(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }

    /// <summary>Reads the few index fields without allocating a dictionary per frame.</summary>
    internal static bool TryReadPoint(ReadOnlySpan<char> line, bool packets, out FramePoint point, out bool discarded)
    {
        long pts = 0, duration = 0;
        bool timestamp = false, key = false;
        discarded = false;
        foreach (var range in line.Split('|'))
        {
            var field = line[range];
            int equals = field.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var name = field[..equals];
            var value = field[(equals + 1)..];
            if (name.SequenceEqual(packets ? "pts" : "best_effort_timestamp"))
            {
                timestamp = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pts);
            }
            else if (name.SequenceEqual("duration"))
            {
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out duration);
            }
            else if (packets && name.SequenceEqual("flags"))
            {
                discarded = value.Contains('D');
                key = value.Contains('K');
            }
            else if (!packets && name.SequenceEqual("key_frame"))
            {
                key = value.SequenceEqual("1");
            }
        }
        point = new(pts, duration, key);
        return timestamp;
    }
}
