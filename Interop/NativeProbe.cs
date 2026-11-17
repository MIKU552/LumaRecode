using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumaRecode.Native;

[Flags]
public enum NativeProbeFlags : uint
{
    None = 0, Hdr10Plus = 1, DolbyVision = 2, Raw = 4, RawCalibrationPending = 8
}
public enum NativeProbePhase : uint
{
    Pending = 0, Streams = 1, Frames = 2, Dji = 3, PixelFormats = 4, Assemble = 5, Complete = 6
}
public sealed record NativeProbeOptions(string Ffprobe, string Source, ulong TimeoutMilliseconds = 25000,
    int MetadataCapacity = 8 * 1024 * 1024);
public sealed record NativeProbeSnapshot(NativeStatus Status, NativeProbeFlags Flags, bool CleanupConfirmed,
    int VideoIndex, int DjiIndex, uint AudioCount, NativeProbePhase Phase, double Duration, ulong ElapsedMilliseconds);
public sealed record NativeProbeResult(NativeStatus Status, NativeProbeSnapshot Snapshot, byte[] Data, string Diagnostic);

/// <summary>Dispatches native source inspection and returns its immutable JSON snapshot.</summary>
/// <remarks>Selection, merging, classification and subprocess ordering belong to the C++ core.</remarks>
public static partial class NativeProbe
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<NativeProbeResult> RunAsync(NativeProbeOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var handle = CreateHandle(options);
        using var registration = cancellation.UnsafeRegister(static state => Cancel((ProbeHandle)state!), handle);
        // Always enter native run(), including pre-cancellation, so callers get
        // a terminal status and confirmed child cleanup rather than an abandoned task.
        return await Task.Run(() => Execute(handle), CancellationToken.None).ConfigureAwait(false);
    }

    public static unsafe byte[] NormalizeJson(ReadOnlySpan<byte> json)
    {
        var error = NativeCore.CreateError();
        fixed (byte* source = json)
        {
            Utf8View input = new()
            {
                Data = source,
                Size = (ulong)json.Length
            };
            var status = Normalize(input, null, 0, out var required, ref error);
            if (status != NativeStatus.BufferTooSmall)
                throw new InvalidDataException(NativeCore.ReadDiagnostic(in error));
            var bytes = new byte[checked((int)required)];
            fixed (byte* output = bytes)
            {
                status = Normalize(input, output, (ulong)bytes.Length, out var copied, ref error);
                if (status != NativeStatus.Ok || copied != required)
                    throw new InvalidDataException(NativeCore.ReadDiagnostic(in error));
            }
            return bytes;
        }
    }

    private static byte[] EncodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0') || Utf8.GetByteCount(path) > 1048576)
            throw new ArgumentException("Expected a bounded path without NUL.", nameof(path));
        return Utf8.GetBytes(path);
    }
    private static unsafe ProbeHandle CreateHandle(NativeProbeOptions options)
    {
        if (options.MetadataCapacity is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        var tool = EncodePath(options.Ffprobe);
        var source = EncodePath(options.Source);
        fixed (byte* toolPointer = tool, sourcePointer = source)
        {
            ProbeOptions value = default;
            NativeCore.Check(InitializeOptions(ref value, (ulong)sizeof(ProbeOptions)));
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
            value.TimeoutMilliseconds = options.TimeoutMilliseconds;
            value.MetadataCapacity = (ulong)options.MetadataCapacity;
            var error = NativeCore.CreateError();
            var status = Create(in value, out var pointer, ref error);
            if (status != NativeStatus.Ok)
                throw new ArgumentException(NativeCore.ReadDiagnostic(in error), nameof(options));
            try
            {
                return new ProbeHandle(pointer);
            }
            catch { Release(pointer); throw; }
        }
    }
    private static unsafe NativeProbeResult Execute(ProbeHandle handle)
    {
        var error = NativeCore.CreateError();
        var status = Run(handle, ref error);
        ProbeSnapshot snapshot = default;
        NativeCore.Check(InitializeSnapshot(ref snapshot, (ulong)sizeof(ProbeSnapshot)));
        NativeCore.Check(GetSnapshot(handle, ref snapshot));
        if (snapshot.State != 2 || snapshot.Status != status)
            throw new InvalidOperationException("The native probe did not return a matching terminal state.");
        byte[] data = [];
        if (status == NativeStatus.Ok)
        {
            data = new byte[checked((int)snapshot.JsonSize)];
            fixed (byte* destination = data)
            {
                NativeCore.Check(CopyJson(handle, destination, (ulong)data.Length, out var copied));
                if (copied != (ulong)data.Length)
                    throw new InvalidOperationException("Native probe JSON changed after completion.");
            }
        }
        var result = new NativeProbeSnapshot(status, snapshot.Flags, snapshot.CleanupState == 1,
            snapshot.VideoIndex, snapshot.DjiIndex, snapshot.AudioCount, snapshot.Phase, snapshot.Duration, snapshot.ElapsedMilliseconds);
        return new(status, result, data, NativeCore.ReadDiagnostic(in error));
    }

    private sealed class ProbeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ProbeHandle(IntPtr value) : base(true) => SetHandle(value);
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
    private struct ProbeOptions
    {
        public uint Size, AbiVersion;
        public Utf8View Ffprobe, Source;
        public ulong TimeoutMilliseconds, MetadataCapacity, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeSnapshot
    {
        public uint Size, AbiVersion, State;
        public NativeStatus Status;
        public NativeProbeFlags Flags;
        public uint CleanupState;
        public int VideoIndex, DjiIndex;
        public uint AudioCount;
        public NativeProbePhase Phase;
        public double Duration;
        public ulong ElapsedMilliseconds, JsonSize;
    }
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_options_init")]
    private static partial NativeStatus InitializeOptions(ref ProbeOptions value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_snapshot_init")]
    private static partial NativeStatus InitializeSnapshot(ref ProbeSnapshot value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_create")]
    private static partial NativeStatus Create(in ProbeOptions options, out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_run")]
    private static partial NativeStatus Run(ProbeHandle handle, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_cancel")]
    private static partial NativeStatus Cancel(ProbeHandle handle);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_get_snapshot")]
    private static partial NativeStatus GetSnapshot(ProbeHandle handle, ref ProbeSnapshot result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_copy_json")]
    private static unsafe partial NativeStatus CopyJson(ProbeHandle handle, byte* destination, ulong capacity, out ulong required);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_release")]
    private static partial void Release(IntPtr handle);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_normalize_json")]
    private static unsafe partial NativeStatus Normalize(Utf8View input, byte* destination, ulong capacity,
        out ulong required, ref NativeError error);
}
