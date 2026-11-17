using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumaRecode.Native;

public enum NativeCaptureMode : uint
{
    Full = 0, Tail = 1, Discard = 2
}

/// <summary>One shell-free invocation. Capacities retain bytes; timeout zero permits long jobs.</summary>
public sealed record NativeProcessOptions(string Executable, IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null, NativeCaptureMode OutputMode = NativeCaptureMode.Full,
    int OutputCapacity = 8 * 1024 * 1024, int ErrorCapacity = 8192, ulong TimeoutMilliseconds = 25000);

/// <summary>Final native status, exact output bytes and the retained diagnostic tail.</summary>
public sealed record NativeProcessResult(NativeStatus Status, int ExitCode, byte[] Output, byte[] Error,
    ulong ElapsedMilliseconds, ulong OutputBytesSeen, ulong ErrorBytesSeen, string Diagnostic, bool CleanupConfirmed)
{
    public string ErrorText => Encoding.UTF8.GetString(Error);
}

/// <summary>Owns native invocation lifetimes and dispatches blocking execution off the caller's thread.</summary>
public static partial class NativeProcess
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Returns a terminal result, including Cancelled/TimedOut, after native cleanup has finished.</summary>
    /// <remarks>Cancellation requests stop the process; they do not abandon the worker or its pipes.</remarks>
    public static async Task<NativeProcessResult> RunAsync(NativeProcessOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var handle = CreateHandle(options);
        using var registration = cancellation.UnsafeRegister(static state =>
        {
            Cancel((ProcessHandle)state!);
        }, handle);
        // Do not pass cancellation to Task.Run: a cancelled scheduling token
        // could skip run() and its final native state transition altogether.
        return await Task.Run(() => Execute(handle), CancellationToken.None).ConfigureAwait(false);
    }

    private static unsafe ProcessHandle CreateHandle(NativeProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Executable);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        if (options.Arguments.Count > 1024 || options.OutputCapacity is < 0 or > 64 * 1024 * 1024
            || options.ErrorCapacity is < 0 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid native process arguments or budgets.");
        }
        var arguments = options.Arguments.ToArray();
        var texts = new[] { options.Executable, options.WorkingDirectory ?? "" }.Concat(arguments).ToArray();
        long total = 0;
        foreach (var text in texts)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Contains('\0'))
            {
                throw new ArgumentException("Process strings must not contain NUL.", nameof(options));
            }
            total += Utf8.GetByteCount(text);
        }
        if (total > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Process strings exceed one MiB.");
        }
        var pins = new List<GCHandle>(texts.Length);
        var views = new Utf8View[texts.Length];
        try
        {
            for (int i = 0; i < texts.Length; i++)
            {
                var bytes = Utf8.GetBytes(texts[i]);
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                pins.Add(pin);
                views[i] = new Utf8View { Data = (byte*)pin.AddrOfPinnedObject(), Size = (ulong)bytes.Length };
            }
            ProcessOptions value = default;
            NativeCore.Check(InitializeOptions(ref value, (ulong)sizeof(ProcessOptions)));
            value.Executable = views[0];
            value.WorkingDirectory = views[1];
            value.ArgumentCount = (uint)arguments.Length;
            value.OutputMode = options.OutputMode;
            value.OutputCapacity = (ulong)options.OutputCapacity;
            value.ErrorCapacity = (ulong)options.ErrorCapacity;
            value.TimeoutMilliseconds = options.TimeoutMilliseconds;
            var error = NativeCore.CreateError();
            fixed (Utf8View* argumentsPointer = views)
            {
                value.Arguments = argumentsPointer + 2;
                var status = Create(in value, out var pointer, ref error);
                if (status != NativeStatus.Ok)
                {
                    string diagnostic = NativeCore.ReadDiagnostic(in error);
                    throw status == NativeStatus.InvalidArgument
                        ? new ArgumentException(diagnostic, nameof(options))
                        : new InvalidOperationException($"Native process creation failed ({status}): {diagnostic}");
                }
                try
                {
                    return new ProcessHandle(pointer);
                }
                catch
                {
                    Release(pointer);
                    throw;
                }
            }
        }
        finally
        {
            foreach (var pin in pins)
            {
                pin.Free();
            }
        }
    }

    private static unsafe NativeProcessResult Execute(ProcessHandle handle)
    {
        var error = NativeCore.CreateError();
        var status = Run(handle, ref error);
        ProcessSnapshot snapshot = default;
        NativeCore.Check(InitializeSnapshot(ref snapshot, (ulong)sizeof(ProcessSnapshot)));
        NativeCore.Check(GetSnapshot(handle, ref snapshot));
        if (snapshot.State != 2 || snapshot.Status != status)
        {
            throw new InvalidOperationException("The native process returned without a matching terminal state.");
        }
        return new NativeProcessResult(status, snapshot.ExitCode,
            Copy(handle, 1, snapshot.OutputRetained), Copy(handle, 2, snapshot.ErrorRetained),
            snapshot.ElapsedMilliseconds, snapshot.OutputSeen, snapshot.ErrorSeen,
            NativeCore.ReadDiagnostic(in error), snapshot.CleanupState == 1);
    }

    private static unsafe byte[] Copy(ProcessHandle handle, uint stream, ulong count)
    {
        var bytes = new byte[checked((int)count)];
        fixed (byte* destination = bytes)
        {
            NativeCore.Check(CopyOutput(handle, stream, destination, count, out var copied));
            if (copied != count)
            {
                throw new InvalidOperationException("Native output changed after process completion.");
            }
        }
        return bytes;
    }

    private sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ProcessHandle(IntPtr value) : base(true) => SetHandle(value);
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
    private unsafe struct ProcessOptions
    {
        public uint StructSize, AbiVersion;
        public Utf8View Executable, WorkingDirectory;
        public Utf8View* Arguments;
        public uint ArgumentCount;
        public NativeCaptureMode OutputMode;
        public ulong OutputCapacity, ErrorCapacity, TimeoutMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessSnapshot
    {
        public uint StructSize, AbiVersion, State;
        public NativeStatus Status;
        public int ExitCode;
        public uint CleanupState;
        public ulong ElapsedMilliseconds, OutputSeen, ErrorSeen, OutputRetained, ErrorRetained;
    }

    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_options_init")]
    private static partial NativeStatus InitializeOptions(ref ProcessOptions value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_snapshot_init")]
    private static partial NativeStatus InitializeSnapshot(ref ProcessSnapshot value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_create")]
    private static partial NativeStatus Create(in ProcessOptions options, out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_run")]
    private static partial NativeStatus Run(ProcessHandle handle, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_cancel")]
    private static partial NativeStatus Cancel(ProcessHandle handle);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_get_snapshot")]
    private static partial NativeStatus GetSnapshot(ProcessHandle handle, ref ProcessSnapshot result);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_copy_output")]
    private static unsafe partial NativeStatus CopyOutput(ProcessHandle handle, uint stream, byte* destination,
        ulong capacity, out ulong required);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_process_release")]
    private static partial void Release(IntPtr handle);
}
