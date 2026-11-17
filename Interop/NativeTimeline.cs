using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumaRecode.Native;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeFramePoint(long Pts, long Duration, uint KeyFrame, uint Reserved = 0);
public sealed record NativeFrameWindow(ulong Start, ulong End, ulong SeekFrame,
    double SeekSeconds, double StartSeconds, double EndSeconds);
public sealed record NativeTimelineOptions(string Ffprobe, string Source, string Codec, int VideoIndex,
    long TimeBaseNumerator, long TimeBaseDenominator, bool Progressive = true,
    ulong IndexCapacity = 512 * 1024 * 1024, ulong TimeoutMilliseconds = 0);
public sealed record NativeTimelineSnapshot(NativeStatus Status, bool PacketIndexed, bool CleanupConfirmed,
    ulong Count, long TimeBaseNumerator, long TimeBaseDenominator, double Duration, ulong ElapsedMilliseconds);
public sealed record NativeTimelineResult(NativeStatus Status, NativeTimelineSnapshot Snapshot,
    NativeTimeline? Timeline, string Diagnostic);

/// <summary>Owns an immutable native display index; queries and slices avoid a full managed copy.</summary>
/// <remarks>The loader, PTS order, fallback and range resolution are implemented by C++.</remarks>
public sealed partial class NativeTimeline : IDisposable
{
    private readonly TimelineHandle handle;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private NativeTimeline(TimelineHandle handle) => this.handle = handle;

    public static async Task<NativeTimelineResult> LoadAsync(NativeTimelineOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await LoadHandleAsync(CreateHandle(options), cancellation).ConfigureAwait(false);
    }

    public static async Task<NativeTimelineResult> LoadFromProbeAsync(string ffprobe, byte[] mediaJson,
        CancellationToken cancellation = default, ulong indexCapacity = 512 * 1024 * 1024, ulong timeoutMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(mediaJson);
        return await LoadHandleAsync(CreateProbeHandle(ffprobe, mediaJson, indexCapacity, timeoutMilliseconds), cancellation).ConfigureAwait(false);
    }

    private static async Task<NativeTimelineResult> LoadHandleAsync(TimelineHandle handle, CancellationToken cancellation)
    {
        try
        {
            using var registration = cancellation.UnsafeRegister(static state => Cancel((TimelineHandle)state!), handle);
            // Enter native run even for pre-cancellation so cleanup and status
            // are reported through the same terminal-state contract.
            var result = await Task.Run(() => Execute(handle), CancellationToken.None).ConfigureAwait(false);
            registration.Dispose();
            if (result.Status != NativeStatus.Ok)
                handle.Dispose();
            return result;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static unsafe NativeTimeline FromPoints(ReadOnlySpan<NativeFramePoint> points,
        long numerator, long denominator, bool packetIndexed)
    {
        var error = NativeCore.CreateError();
        fixed (NativeFramePoint* pointer = points)
        {
            var status = Import(pointer, (ulong)points.Length, numerator, denominator,
                packetIndexed ? 1U : 0, out var created, ref error);
            if (status != NativeStatus.Ok)
                throw new ArgumentException(NativeCore.ReadDiagnostic(in error), nameof(points));
            return Own(created);
        }
    }

    public NativeTimelineSnapshot Snapshot => ReadSnapshot(handle);

    public unsafe NativeFramePoint[] CopyPoints(ulong start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var points = new NativeFramePoint[count];
        fixed (NativeFramePoint* destination = points)
            NativeCore.Check(Copy(handle, start, (ulong)count, destination, (ulong)count));
        return points;
    }

    public ulong AtOrAfter(double position)
    {
        NativeCore.Check(Find(handle, position, out var frame));
        return frame;
    }

    public unsafe NativeFrameWindow Resolve(ulong start = 0, ulong? end = null)
    {
        FrameWindow value = default;
        NativeCore.Check(InitializeWindow(ref value, (ulong)sizeof(FrameWindow)));
        NativeCore.Check(ResolveWindow(handle, start, end ?? ulong.MaxValue, ref value));
        return new(value.Start, value.End, value.SeekFrame, value.SeekSeconds, value.StartSeconds, value.EndSeconds);
    }

    public void Dispose() => handle.Dispose();

    private static NativeTimeline Own(IntPtr pointer)
    {
        TimelineHandle? owner = null;
        try
        {
            owner = new(pointer);
            return new(owner);
        }
        catch
        {
            if (owner is null)
                Release(pointer);
            else
                owner.Dispose();
            throw;
        }
    }

    private static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Contains('\0') || Utf8.GetByteCount(value) > 500000)
            throw new ArgumentException("Expected bounded UTF-8 text without NUL.", nameof(value));
        return Utf8.GetBytes(value);
    }

    private static unsafe TimelineHandle CreateHandle(NativeTimelineOptions options)
    {
        var tool = Encode(options.Ffprobe);
        var source = Encode(options.Source);
        var codec = Encode(options.Codec);
        fixed (byte* toolPointer = tool, sourcePointer = source, codecPointer = codec)
        {
            TimelineOptions value = default;
            NativeCore.Check(InitializeOptions(ref value, (ulong)sizeof(TimelineOptions)));
            value.Ffprobe = new()
            {
                Data = toolPointer,
                Size = (ulong)tool.Length
            };
            value.Source = new()
            {
                Data = sourcePointer,
                Size = (ulong)source.Length
            };
            value.Codec = new()
            {
                Data = codecPointer,
                Size = (ulong)codec.Length
            };
            value.VideoIndex = options.VideoIndex;
            value.TimeBaseNumerator = options.TimeBaseNumerator;
            value.TimeBaseDenominator = options.TimeBaseDenominator;
            value.Flags = options.Progressive ? 1U : 0;
            value.IndexCapacity = options.IndexCapacity;
            value.TimeoutMilliseconds = options.TimeoutMilliseconds;
            var error = NativeCore.CreateError();
            var status = Create(in value, out var pointer, ref error);
            if (status != NativeStatus.Ok)
                throw new ArgumentException(NativeCore.ReadDiagnostic(in error), nameof(options));
            try
            {
                return new(pointer);
            }
            catch { Release(pointer); throw; }
        }
    }

    private static unsafe TimelineHandle CreateProbeHandle(string ffprobe, byte[] mediaJson, ulong capacity, ulong timeout)
    {
        var tool = Encode(ffprobe);
        fixed (byte* toolPointer = tool, mediaPointer = mediaJson)
        {
            TimelineOptions options = default;
            NativeCore.Check(InitializeOptions(ref options, (ulong)sizeof(TimelineOptions)));
            options.Ffprobe = new()
            {
                Data = toolPointer,
                Size = (ulong)tool.Length
            };
            options.IndexCapacity = capacity;
            options.TimeoutMilliseconds = timeout;
            var error = NativeCore.CreateError();
            var status = CreateFromProbe(in options, new()
            {
                Data = mediaPointer,
                Size = (ulong)mediaJson.Length
            },
                out var pointer, ref error);
            if (status != NativeStatus.Ok)
                throw new ArgumentException(NativeCore.ReadDiagnostic(in error), nameof(mediaJson));
            try
            {
                return new(pointer);
            }
            catch { Release(pointer); throw; }
        }
    }

    private static NativeTimelineResult Execute(TimelineHandle handle)
    {
        var error = NativeCore.CreateError();
        var status = Run(handle, ref error);
        var snapshot = ReadSnapshot(handle);
        if (snapshot.Status != status)
            throw new InvalidOperationException("Native timeline terminal status mismatch.");
        return new(status, snapshot, status == NativeStatus.Ok ? new NativeTimeline(handle) : null,
            NativeCore.ReadDiagnostic(in error));
    }

    private static unsafe NativeTimelineSnapshot ReadSnapshot(TimelineHandle handle)
    {
        TimelineSnapshot value = default;
        NativeCore.Check(InitializeSnapshot(ref value, (ulong)sizeof(TimelineSnapshot)));
        NativeCore.Check(GetSnapshot(handle, ref value));
        if (value.State != 2)
            throw new InvalidOperationException("Native timeline has not finished.");
        return new(value.Status, (value.Flags & 1) != 0, value.CleanupState == 1, value.Count,
            value.TimeBaseNumerator, value.TimeBaseDenominator, value.Duration, value.ElapsedMilliseconds);
    }

    private sealed class TimelineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TimelineHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            Release(handle);
            return true;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Utf8View
    {
        public byte* Data; public ulong Size;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TimelineOptions
    {
        public uint Size, AbiVersion;
        public Utf8View Ffprobe, Source, Codec;
        public long TimeBaseNumerator, TimeBaseDenominator;
        public int VideoIndex;
        public uint Flags;
        public ulong IndexCapacity, TimeoutMilliseconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TimelineSnapshot
    {
        public uint Size, AbiVersion, State;
        public NativeStatus Status;
        public uint Flags, CleanupState;
        public ulong Count;
        public long TimeBaseNumerator, TimeBaseDenominator;
        public double Duration;
        public ulong ElapsedMilliseconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameWindow
    {
        public uint Size, AbiVersion;
        public ulong Start, End, SeekFrame;
        public double SeekSeconds, StartSeconds, EndSeconds;
    }
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_options_init")]
    private static partial NativeStatus InitializeOptions(ref TimelineOptions value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_snapshot_init")]
    private static partial NativeStatus InitializeSnapshot(ref TimelineSnapshot value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_frame_window_init")]
    private static partial NativeStatus InitializeWindow(ref FrameWindow value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_create")]
    private static partial NativeStatus Create(in TimelineOptions options, out IntPtr value, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_run")]
    private static partial NativeStatus Run(TimelineHandle handle, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_create_from_probe")]
    private static partial NativeStatus CreateFromProbe(in TimelineOptions options, Utf8View media,
        out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_cancel")]
    private static partial NativeStatus Cancel(TimelineHandle handle);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_get_snapshot")]
    private static partial NativeStatus GetSnapshot(TimelineHandle handle, ref TimelineSnapshot result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_from_points")]
    private static unsafe partial NativeStatus Import(NativeFramePoint* points, ulong count, long numerator,
        long denominator, uint flags, out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_copy_points")]
    private static unsafe partial NativeStatus Copy(TimelineHandle handle, ulong start, ulong count,
        NativeFramePoint* destination, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_at_or_after")]
    private static partial NativeStatus Find(TimelineHandle handle, double position, out ulong result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_resolve")]
    private static partial NativeStatus ResolveWindow(TimelineHandle handle, ulong start, ulong end, ref FrameWindow result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_timeline_release")]
    private static partial void Release(IntPtr handle);
}
