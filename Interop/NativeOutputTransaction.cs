using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumaRecode.Native;

public enum NativeOutputState : uint
{
    Created, Sealed, Committed, Aborted, Retained
}

public sealed record NativeOutputSnapshot(NativeOutputState State, bool WorkspaceExists,
    ulong SourceSize, ulong OutputSize);

/// <summary>Publishes verified media from a private workspace beside its destination.</summary>
/// <remarks>
/// Complete encoding and media verification before Seal. Pass confirmed cleanup only after
/// every process using the workspace has stopped. Dispose releases handles but leaves files
/// intact; call Cleanup explicitly when abandoning an output.
/// </remarks>
public sealed partial class NativeOutputTransaction : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly OutputHandle handle;

    private NativeOutputTransaction(OutputHandle handle) => this.handle = handle;

    public string WorkspacePath => CopyPath(1);
    public string StagingPath => CopyPath(2);
    public string DestinationPath => CopyPath(3);

    public unsafe NativeOutputSnapshot Snapshot
    {
        get
        {
            OutputSnapshot snapshot = default;
            NativeCore.Check(InitializeSnapshot(ref snapshot, (ulong)sizeof(OutputSnapshot)));
            NativeCore.Check(GetSnapshot(handle, ref snapshot));
            return new(snapshot.State, (snapshot.Flags & 1) != 0, snapshot.SourceSize, snapshot.OutputSize);
        }
    }

    /// <summary>Copies absolute UTF-8 paths and fixes overwrite consent for the transaction.</summary>
    public static unsafe NativeOutputTransaction Create(string source, string destination, bool overwrite = false)
    {
        var first = EncodePath(source);
        var second = EncodePath(destination);
        fixed (byte* sourcePointer = first, destinationPointer = second)
        {
            OutputOptions options = default;
            NativeCore.Check(InitializeOptions(ref options, (ulong)sizeof(OutputOptions)));
            options.Source = new()
            {
                Data = sourcePointer,
                Size = (ulong)first.Length
            };
            options.Destination = new()
            {
                Data = destinationPointer,
                Size = (ulong)second.Length
            };
            options.Overwrite = overwrite ? 1U : 0U;
            var error = NativeCore.CreateError();
            var status = CreateNative(in options, out var pointer, ref error);
            if (status != NativeStatus.Ok)
            {
                throw new NativeOutputException(status, NativeCore.ReadDiagnostic(in error));
            }
            OutputHandle? owned = null;
            try
            {
                owned = new OutputHandle(pointer);
                return new NativeOutputTransaction(owned);
            }
            catch
            {
                if (owned is null)
                    Release(pointer);
                else
                    owned.Dispose();
                throw;
            }
        }
    }

    public static unsafe NativeStatus FilesSame(string left, string right, out bool same, out string diagnostic)
    {
        var first = EncodePath(left);
        var second = EncodePath(right);
        var error = NativeCore.CreateError();
        fixed (byte* leftPointer = first, rightPointer = second)
        {
            var status = FilesSameNative(new()
            {
                Data = leftPointer,
                Size = (ulong)first.Length
            },
                new()
                {
                    Data = rightPointer,
                    Size = (ulong)second.Length
                }, out var result, ref error);
            same = status == NativeStatus.Ok && result != 0;
            diagnostic = NativeCore.ReadDiagnostic(in error);
            return status;
        }
    }

    public NativeStatus Seal(bool cleanupConfirmed, out string diagnostic)
    {
        var error = NativeCore.CreateError();
        var status = SealNative(handle, cleanupConfirmed ? 1U : 0U, ref error);
        diagnostic = NativeCore.ReadDiagnostic(in error);
        return status;
    }

    public NativeStatus Commit(out string diagnostic)
    {
        var error = NativeCore.CreateError();
        var status = CommitNative(handle, ref error);
        diagnostic = NativeCore.ReadDiagnostic(in error);
        return status;
    }

    /// <summary>Deletes the primary staging file and an empty workspace; preserves unknown entries.</summary>
    public NativeStatus Cleanup(bool cleanupConfirmed, out string diagnostic)
    {
        var error = NativeCore.CreateError();
        var status = CleanupNative(handle, cleanupConfirmed ? 1U : 0U, ref error);
        diagnostic = NativeCore.ReadDiagnostic(in error);
        return status;
    }

    public void Dispose() => handle.Dispose();

    private static byte[] EncodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0') || Utf8.GetByteCount(path) > 1024 * 1024)
        {
            throw new ArgumentException("Expected a bounded path without NUL.", nameof(path));
        }
        return Utf8.GetBytes(path);
    }

    private unsafe string CopyPath(uint kind)
    {
        var status = CopyPathNative(handle, kind, null, 0, out var required);
        if (status != NativeStatus.BufferTooSmall)
            NativeCore.Check(status);
        var bytes = new byte[checked((int)required)];
        fixed (byte* destination = bytes)
        {
            NativeCore.Check(CopyPathNative(handle, kind, destination, (ulong)bytes.Length, out var copied));
            if (copied != (ulong)bytes.Length)
                throw new InvalidOperationException("Native path length changed.");
        }
        return Utf8.GetString(bytes);
    }

    private sealed class OutputHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal OutputHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            Release(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Utf8View
    {
        public byte* Data;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputOptions
    {
        public uint Size, AbiVersion;
        public Utf8View Source, Destination;
        public uint Overwrite, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputSnapshot
    {
        public uint Size, AbiVersion;
        public NativeOutputState State;
        public uint Flags;
        public ulong SourceSize, OutputSize;
    }

    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_options_init")]
    private static partial NativeStatus InitializeOptions(ref OutputOptions value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_snapshot_init")]
    private static partial NativeStatus InitializeSnapshot(ref OutputSnapshot value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_create")]
    private static partial NativeStatus CreateNative(in OutputOptions options, out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_files_same")]
    private static partial NativeStatus FilesSameNative(Utf8View left, Utf8View right, out uint result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_get_snapshot")]
    private static partial NativeStatus GetSnapshot(OutputHandle handle, ref OutputSnapshot result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_copy_path")]
    private static unsafe partial NativeStatus CopyPathNative(OutputHandle handle, uint kind, byte* destination,
        ulong capacity, out ulong required);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_seal")]
    private static partial NativeStatus SealNative(OutputHandle handle, uint cleanupConfirmed, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_commit")]
    private static partial NativeStatus CommitNative(OutputHandle handle, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_cleanup")]
    private static partial NativeStatus CleanupNative(OutputHandle handle, uint cleanupConfirmed, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_output_release")]
    private static partial void Release(IntPtr handle);
}

public sealed class NativeOutputException(NativeStatus status, string diagnostic) : IOException(diagnostic)
{
    public NativeStatus Status { get; } = status;
}
