using System.Runtime.InteropServices;
using System.Text;

namespace LumaRecode.Native;

[Flags]
public enum NativePlanFlags : uint
{
    None = 0, QsvTiming = 1, AutoLowPower = 2, MotionPhoto = 4
}
public sealed record NativePlanRequest(byte[] MediaJson, byte[] EncodeJson, byte[] PresetsJson,
    string Ffmpeg, string Destination, string Assets, byte[]? MotionPresetsJson = null,
    double Seconds = 0, NativePlanFlags Flags = NativePlanFlags.None, uint PreviewSize = 0,
    NativeFrameWindow? Window = null, string MotionSize = "source");
public sealed record NativePlanResult(NativeStatus Status, byte[] Data, string Diagnostic);

/// <summary>Passes media, versioned presets and encode choices to the native planner.</summary>
public static partial class NativePlanner
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static NativePlanResult Create(NativePlanRequest request) => Execute(request, null);

    public static NativePlanResult VerifyOutput(NativePlanRequest request, byte[] outputJson)
    {
        ArgumentNullException.ThrowIfNull(outputJson);
        return Execute(request, outputJson);
    }

    private static unsafe NativePlanResult Execute(NativePlanRequest request, byte[]? outputJson)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[][] values = [request.MediaJson, request.EncodeJson, request.PresetsJson, request.MotionPresetsJson ?? [],
            Utf8.GetBytes(request.Ffmpeg), Utf8.GetBytes(request.Destination), Utf8.GetBytes(request.Assets), Utf8.GetBytes(request.MotionSize)];
        var pins = new List<GCHandle>(values.Length);
        var views = new Utf8View[values.Length];
        IntPtr plan = IntPtr.Zero;
        try
        {
            for (int i = 0; i < values.Length; ++i)
            {
                ArgumentNullException.ThrowIfNull(values[i]);
                var pin = GCHandle.Alloc(values[i], GCHandleType.Pinned);
                pins.Add(pin);
                views[i] = new()
                {
                    Data = (byte*)pin.AddrOfPinnedObject(),
                    Size = (ulong)values[i].Length
                };
            }
            PlanOptions options = default;
            NativeCore.Check(Initialize(ref options, (ulong)sizeof(PlanOptions)));
            options.MediaJson = views[0];
            options.EncodeJson = views[1];
            options.PresetsJson = views[2];
            options.MotionPresetsJson = views[3];
            options.Ffmpeg = views[4];
            options.Destination = views[5];
            options.Assets = views[6];
            options.MotionSize = views[7];
            options.Seconds = request.Seconds;
            options.Flags = request.Flags;
            options.PreviewSize = request.PreviewSize;
            FrameWindow window = default;
            if (request.Window is { } value)
            {
                window = new()
                {
                    Size = (uint)sizeof(FrameWindow),
                    AbiVersion = NativeCore.AbiVersion,
                    Start = value.Start,
                    End = value.End,
                    SeekFrame = value.SeekFrame,
                    SeekSeconds = value.SeekSeconds,
                    StartSeconds = value.StartSeconds,
                    EndSeconds = value.EndSeconds
                };
                options.Window = &window;
            }
            var error = NativeCore.CreateError();
            var status = Create(in options, out plan, ref error);
            if (status != NativeStatus.Ok)
            {
                return new(status, [], NativeCore.ReadDiagnostic(in error));
            }

            if (outputJson is not null)
            {
                fixed (byte* output = outputJson)
                {
                    status = Verify(plan, views[0], new()
                    {
                        Data = output,
                        Size = (ulong)outputJson.Length
                    }, ref error);
                }

                return new(status, [], NativeCore.ReadDiagnostic(in error));
            }
            if (CopyJson(plan, null, 0, out var required) != NativeStatus.BufferTooSmall)
            {
                throw new InvalidOperationException("Native plan size query failed.");
            }

            var bytes = new byte[checked((int)required)];
            fixed (byte* destination = bytes)
            {
                NativeCore.Check(CopyJson(plan, destination, (ulong)bytes.Length, out _));
            }

            return new(status, bytes, "");
        }
        finally
        {
            if (plan != IntPtr.Zero)
            {
                Release(plan);
            }

            foreach (var pin in pins)
            {
                pin.Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Utf8View
    {
        public byte* Data; public ulong Size;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PlanOptions
    {
        public uint Size, AbiVersion;
        public Utf8View MediaJson, EncodeJson, PresetsJson, MotionPresetsJson, Ffmpeg, Destination, Assets;
        public double Seconds;
        public NativePlanFlags Flags;
        public uint PreviewSize;
        public FrameWindow* Window;
        public Utf8View MotionSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameWindow
    {
        public uint Size, AbiVersion;
        public ulong Start, End, SeekFrame;
        public double SeekSeconds, StartSeconds, EndSeconds;
    }
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_plan_options_init")]
    private static partial NativeStatus Initialize(ref PlanOptions value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_plan_create")]
    private static partial NativeStatus Create(in PlanOptions options, out IntPtr result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_plan_copy_json")]
    private static unsafe partial NativeStatus CopyJson(IntPtr plan, byte* destination, ulong capacity, out ulong required);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_plan_release")]
    private static partial void Release(IntPtr plan);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_plan_verify_output")]
    private static partial NativeStatus Verify(IntPtr plan, Utf8View source, Utf8View output, ref NativeError error);
}
