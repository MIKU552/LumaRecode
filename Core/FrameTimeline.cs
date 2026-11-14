using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoConverter.Core;

/// <summary>A zero-based, end-exclusive selection of decoded display frames.</summary>
public sealed record FrameRange(int Start = 0, int? End = null);

/// <summary>Original stream ticks and random-access evidence for one displayed frame.</summary>
public readonly record struct FramePoint(long Pts, long Duration, bool KeyFrame);

/// <summary>A frame selection resolved to source times and a safe preceding seek point.</summary>
public sealed record FrameWindow(int Start, int End, int SeekFrame, double SeekSeconds, double StartSeconds, double EndSeconds)
{
    public int Count => End - Start;
    public double Duration => EndSeconds - StartSeconds;
}

/// <summary>Indexes display frames lazily; common progressive codecs use compressed packet timestamps.</summary>
/// <remarks>Discarded MP4 preroll is excluded. Missing/duplicate packet PTS, interlaced
/// streams and other codecs use decoded frame order instead of guessing from FPS.</remarks>
public sealed class FrameTimeline
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

    private static FrameTimeline Capture(FrameTimeline timeline, string path)
    {
        var file = new FileInfo(path);
        timeline.sourcePath = file.FullName;
        timeline.sourceBytes = file.Length;
        timeline.sourceModified = file.LastWriteTimeUtc;
        return timeline;
    }

    public FrameTimeline(IReadOnlyList<FramePoint> frames, long numerator, long denominator, bool packetIndexed)
    {
        if (frames.Count == 0 || numerator <= 0 || denominator <= 0)
        {
            throw new InvalidDataException("无法建立视频帧时间轴。");
        }
        Frames = frames;
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

    public static async Task<FrameTimeline> LoadAsync(MediaTools tools, MediaInfo media, CancellationToken cancellation = default)
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
                if (!packets.Zip(packets.Skip(1)).Any(pair => pair.First.Pts == pair.Second.Pts))
                {
                    return Capture(new(packets, numerator, denominator, packetIndexed: true), media.Path);
                }
            }
        }
        var frames = await ReadAsync(tools, media, packets: false, cancellation).ConfigureAwait(false);
        return Capture(new(frames ?? throw new InvalidDataException("无法读取逐帧显示时间。"), numerator, denominator, packetIndexed: false), media.Path);
    }

    private static async Task<List<FramePoint>?> ReadAsync(MediaTools tools, MediaInfo media, bool packets, CancellationToken cancellation)
    {
        var arguments = new[] { "-v", "error", "-threads", "2", "-select_streams", media.Video.Text("index"), packets ? "-show_packets" : "-show_frames",
            "-show_entries", packets ? "packet=pts,duration,flags:packet_side_data=" : "frame=best_effort_timestamp,duration,key_frame:frame_side_data=",
            "-of", "compact=p=1:nk=0", media.Path };
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(tools.Ffprobe, arguments) };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellation.Register(() => ProcessRunner.Stop(process));
        var errors = ReadErrorAsync(process.StandardError);
        var result = new List<FramePoint>();
        bool complete = true;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith(packets ? "packet|" : "frame|", StringComparison.Ordinal))
                {
                    continue;
                }
                var fields = new Dictionary<string, string>();
                foreach (var part in line.Split('|').Skip(1))
                {
                    int equals = part.IndexOf('=');
                    if (equals > 0)
                    {
                        fields[part[..equals]] = part[(equals + 1)..];
                    }
                }
                string flags = fields.GetValueOrDefault("flags", "");
                if (packets && flags.Contains('D'))
                {
                    continue;
                }
                if (!long.TryParse(fields.GetValueOrDefault(packets ? "pts" : "best_effort_timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts))
                {
                    complete = false;
                    continue;
                }
                long.TryParse(fields.GetValueOrDefault("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration);
                result.Add(new(pts, duration, packets ? flags.Contains('K') : fields.GetValueOrDefault("key_frame") == "1"));
            }
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("无法建立视频帧时间轴：" + await errors.ConfigureAwait(false));
            }
            return complete ? result : null;
        }
        finally
        {
            ProcessRunner.Stop(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadErrorAsync(TextReader reader)
    {
        var buffer = new char[2048];
        var tail = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
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
