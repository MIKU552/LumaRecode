using LumaRecode.Native;

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

/// <summary>A host-owned view of the shared display index; frame records are fetched in bounded pages.</summary>
/// <remarks>Holding this object keeps its native SafeHandle alive. Concurrent GUI, preview and export
/// borrowers share it; the native owner reports allocation pressure to the GC when the last borrower leaves.</remarks>
public sealed class FrameTimeline
{
    private readonly NativeTimeline native;
    private readonly NativeTimelineSnapshot snapshot;
    private string? sourcePath;
    private long sourceBytes;
    private DateTime sourceModified;
    public IReadOnlyList<FramePoint> Frames
    {
        get;
    }
    public long TimeBaseNumerator => snapshot.TimeBaseNumerator;
    public long TimeBaseDenominator => snapshot.TimeBaseDenominator;
    public bool PacketIndexed => snapshot.PacketIndexed;
    public int Count => checked((int)snapshot.Count);
    public double Duration => snapshot.Duration;

    public FrameTimeline(IReadOnlyList<FramePoint> frames, long numerator, long denominator, bool packetIndexed)
        : this(Import(frames, numerator, denominator, packetIndexed)) { }

    private FrameTimeline(NativeTimeline native)
    {
        this.native = native;
        snapshot = native.Snapshot;
        Frames = new FrameView(this);
    }

    private static NativeTimeline Import(IReadOnlyList<FramePoint> frames, long numerator, long denominator, bool packets)
    {
        if (frames.Count == 0 || (long)frames.Count * 48 > MediaResourceLimits.IndexBytes)
        {
            throw new InvalidDataException("无法建立视频帧时间轴。");
        }

        var values = new NativeFramePoint[frames.Count];
        for (int i = 0; i < values.Length; ++i)
        {
            values[i] = new(frames[i].Pts, frames[i].Duration, frames[i].KeyFrame ? 1U : 0);
        }

        return NativeTimeline.FromPoints(values, numerator, denominator, packets);
    }

    public bool Matches(string path)
    {
        var file = new FileInfo(path);
        return sourcePath is not null && Path.GetFullPath(path).Equals(sourcePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && file.Exists && file.Length == sourceBytes && file.LastWriteTimeUtc == sourceModified;
    }

    public double Seconds(int frame) => (double)Frames[frame].Pts * TimeBaseNumerator / TimeBaseDenominator;
    public double Position(int frame) => Seconds(frame) - Seconds(0);
    public double BoundarySeconds(int frame) => frame == 0 ? Seconds(0) : native.Resolve(0, checked((ulong)frame)).EndSeconds;
    public int AtOrAfter(double position) => checked((int)native.AtOrAfter(position));
    public FrameWindow Resolve(FrameRange range)
    {
        if (range.Start < 0 || range.End is < 0 || (range.End ?? Count) <= range.Start || range.End > Count)
        {
            throw new InvalidDataException($"帧范围无效：开始帧须小于结束帧，结束帧不能超过 {Count}。");
        }

        var value = native.Resolve((ulong)range.Start, range.End is { } end ? (ulong)end : null);
        return new((int)value.Start, (int)value.End, (int)value.SeekFrame, value.SeekSeconds, value.StartSeconds, value.EndSeconds);
    }

    public static async Task<FrameTimeline> LoadAsync(MediaTools tools, MediaInfo media, CancellationToken cancellation = default)
    {
        var file = new FileInfo(media.Path);
        long bytes = file.Length;
        var modified = file.LastWriteTimeUtc;
        var result = await NativeTimeline.LoadFromProbeAsync(Path.GetFullPath(tools.Ffprobe), NativeBackend.Snapshot(media), cancellation).ConfigureAwait(false);
        NativeBackend.Check(result.Status, result.Diagnostic, result.Snapshot.CleanupConfirmed, cancellation);
        var timeline = new FrameTimeline(result.Timeline!) { sourcePath = file.FullName, sourceBytes = bytes, sourceModified = modified };
        if (!timeline.Matches(media.Path))
        {
            result.Timeline!.Dispose();
            throw new IOException("素材已更改，请重新添加。");
        }
        return timeline;
    }

    // Compatibility for the parser fixture suite; parsing remains authoritative in C++.
    internal static bool TryReadPoint(ReadOnlySpan<char> line, bool packets, out FramePoint point, out bool discarded)
    {
        bool valid = NativeTimeline.ParsePoint(line.ToString(), packets, out var value, out discarded);
        point = new(value.Pts, value.Duration, value.KeyFrame != 0);
        return valid;
    }

    private sealed class FrameView(FrameTimeline owner) : IReadOnlyList<FramePoint>
    {
        private readonly Lock gate = new();
        private int first = -1;
        private NativeFramePoint[] page = [];
        public int Count => owner.Count;
        public FramePoint this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                lock (gate)
                {
                    if (index < first || index - first >= page.Length)
                    {
                        first = index / 256 * 256;
                        page = owner.native.CopyPoints((ulong)first, Math.Min(256, Count - first));
                    }
                    var value = page[index - first];
                    return new(value.Pts, value.Duration, value.KeyFrame != 0);
                }
            }
        }
        public IEnumerator<FramePoint> GetEnumerator()
        {
            for (int i = 0; i < Count; ++i)
            {
                yield return this[i];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
