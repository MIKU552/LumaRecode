using System.Runtime.InteropServices;
using System.Text;

namespace LumaRecode.Native;

public enum MediaKind : uint
{
    Unknown = 0, Sdr = 1, Hlg = 2, Pq = 3, Log = 4, Raw = 5
}
public enum CameraProfile : uint
{
    None = 0, DjiPocket3 = 1, SamsungLog = 2, MiLog = 3, AppleLog = 4, AppleLog2 = 5
}
public enum DjiSchema : uint
{
    Unknown = 0, Pocket3 = 1, Ac206 = 2, Oq101 = 3
}
public enum MediaEvidence : uint
{
    UnknownTransfer = 0, Raw = 1, AppleLog = 2, AppleLog2 = 3, MiLog = 4, SamsungLog = 5,
    DjiDLogM = 6, DjiOtherDLogM = 7, DjiLog = 8, DjiHlg = 9, DjiSdr = 10, CameraTag = 11,
    LogTag = 12, LogSwitch = 13, TransferHlg = 14, TransferPq = 15, TransferSdr = 16, DolbyVision = 17
}

public sealed record DjiColor(DjiSchema Schema, ulong Mode)
{
    public string SchemaName => Schema switch
    {
        DjiSchema.Pocket3 => "dvtm_PP-101.proto",
        DjiSchema.Ac206 => "dvtm_ac206.proto",
        DjiSchema.Oq101 => "dvtm_oq101.proto",
        _ => ""
    };
}
public sealed record DolbyVisionInfo(uint Profile, uint Compatibility);
public sealed record NativeMediaInput(string Codec, string Transfer,
    IReadOnlyList<KeyValuePair<string, string>> Tags, DjiColor? Dji = null, DolbyVisionInfo? DolbyVision = null);
public sealed record MediaClassification(MediaKind Kind, CameraProfile Profile, MediaEvidence Evidence, int? TagIndex);

/// <summary>Generated bindings for source classification and bounded camera metadata readers.</summary>
/// <remarks>Evidence codes are presentation-neutral; each host supplies localized labels.</remarks>
public static partial class NativeMedia
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static unsafe MediaClassification Classify(NativeMediaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Tags);
        if (input.Tags.Count > 1024)
            throw new ArgumentOutOfRangeException(nameof(input), "Too many camera tags.");
        // Snapshot the caller's collection before pinning borrowed views. Native
        // code consumes them synchronously and retains no managed pointers.
        var tags = input.Tags.ToArray();
        var texts = new[] { input.Codec, input.Transfer, input.Dji?.SchemaName ?? "" }
            .Concat(tags.SelectMany(tag => new[] { tag.Key, tag.Value })).ToArray();
        long total = 0;
        foreach (var text in texts)
        {
            ArgumentNullException.ThrowIfNull(text);
            int count = Utf8.GetByteCount(text);
            if (count > 65536 || (total += count) > 1048576)
                throw new ArgumentOutOfRangeException(nameof(input), "Camera tags exceed the inspection budget.");
        }
        var pins = new List<GCHandle>(texts.Length);
        var views = new Utf8View[texts.Length];
        try
        {
            for (int index = 0; index < texts.Length; ++index)
            {
                var bytes = Utf8.GetBytes(texts[index]);
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                pins.Add(pin);
                views[index] = new()
                {
                    Data = (byte*)pin.AddrOfPinnedObject(),
                    Size = (ulong)bytes.Length
                };
            }
            var pairs = new MediaTag[tags.Length];
            for (int index = 0; index < pairs.Length; ++index)
                pairs[index] = new()
                {
                    Key = views[index * 2 + 3],
                    Value = views[index * 2 + 4]
                };
            ClassifyInput value = default;
            Classification result = default;
            NativeCore.Check(InitializeInput(ref value, (ulong)sizeof(ClassifyInput)));
            NativeCore.Check(InitializeClassification(ref result, (ulong)sizeof(Classification)));
            value.Codec = views[0];
            value.Transfer = views[1];
            value.DjiSchema = views[2];
            if (input.Dji is { } dji)
            {
                value.Flags |= 1;
                value.DjiMode = dji.Mode;
            }
            if (input.DolbyVision is { } dolby)
            {
                value.Flags |= 2;
                value.DolbyVisionProfile = dolby.Profile;
                value.DolbyVisionCompatibility = dolby.Compatibility;
            }
            var error = NativeCore.CreateError();
            fixed (MediaTag* pointer = pairs)
            {
                value.Tags = pointer;
                value.TagCount = (uint)pairs.Length;
                var status = ClassifyNative(in value, ref result, ref error);
                if (status != NativeStatus.Ok)
                    throw new InvalidDataException(NativeCore.ReadDiagnostic(in error));
            }
            return new(result.Kind, result.Profile, result.Evidence,
                result.TagIndex == uint.MaxValue ? null : checked((int)result.TagIndex));
        }
        finally
        {
            foreach (var pin in pins)
                pin.Free();
        }
    }

    /// <summary>Decodes ffprobe's dump without including offset or ASCII columns.</summary>
    public static unsafe byte[] DecodeProbeHex(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        if (Utf8.GetByteCount(dump) > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(dump));
        var bytes = Utf8.GetBytes(dump);
        fixed (byte* source = bytes)
        {
            Utf8View view = new()
            {
                Data = source,
                Size = (ulong)bytes.Length
            };
            var status = DecodeHexNative(view, null, 0, out var required);
            if (status is not (NativeStatus.Ok or NativeStatus.BufferTooSmall))
                throw new InvalidDataException($"Invalid ffprobe hexadecimal data ({status}).");
            var result = new byte[checked((int)required)];
            fixed (byte* destination = result)
            {
                status = DecodeHexNative(view, destination, (ulong)result.Length, out var copied);
                if (status != NativeStatus.Ok || copied != required)
                    throw new InvalidDataException($"Invalid ffprobe hexadecimal data ({status}).");
            }
            return result;
        }
    }

    public static unsafe NativeStatus ParseDjiColor(ReadOnlySpan<byte> packet, out DjiColor? result, out string diagnostic)
    {
        DjiRecord record = default;
        NativeCore.Check(InitializeDji(ref record, (ulong)sizeof(DjiRecord)));
        var error = NativeCore.CreateError();
        fixed (byte* bytes = packet)
        {
            var status = ParseDjiNative(bytes, (ulong)packet.Length, ref record, ref error);
            result = status == NativeStatus.Ok ? new((DjiSchema)record.Schema, record.Mode) : null;
            diagnostic = NativeCore.ReadDiagnostic(in error);
            return status;
        }
    }

    public static unsafe NativeStatus AvcIsMonochrome(ReadOnlySpan<byte> extradata, out bool monochrome, out string diagnostic)
    {
        var error = NativeCore.CreateError();
        fixed (byte* bytes = extradata)
        {
            var status = AvcNative(bytes, (ulong)extradata.Length, out var value, ref error);
            monochrome = status == NativeStatus.Ok && value != 0;
            diagnostic = NativeCore.ReadDiagnostic(in error);
            return status;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Utf8View
    {
        public byte* Data; public ulong Size;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MediaTag
    {
        public Utf8View Key, Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ClassifyInput
    {
        public uint Size, AbiVersion;
        public Utf8View Codec, Transfer;
        public MediaTag* Tags;
        public uint TagCount, Flags;
        public Utf8View DjiSchema;
        public ulong DjiMode;
        public uint DolbyVisionProfile, DolbyVisionCompatibility;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Classification
    {
        public uint Size, AbiVersion;
        public MediaKind Kind;
        public CameraProfile Profile;
        public MediaEvidence Evidence;
        public uint TagIndex;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DjiRecord
    {
        public uint Size, AbiVersion, Schema, Reserved;
        public ulong Mode;
    }

    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_classify_input_init")]
    private static partial NativeStatus InitializeInput(ref ClassifyInput value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_classification_init")]
    private static partial NativeStatus InitializeClassification(ref Classification value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_dji_color_init")]
    private static partial NativeStatus InitializeDji(ref DjiRecord value, ulong capacity);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_media_classify")]
    private static partial NativeStatus ClassifyNative(in ClassifyInput input, ref Classification result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_probe_decode_hex")]
    private static unsafe partial NativeStatus DecodeHexNative(Utf8View dump, byte* destination, ulong capacity, out ulong required);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_dji_parse_color")]
    private static unsafe partial NativeStatus ParseDjiNative(byte* data, ulong size, ref DjiRecord result, ref NativeError error);
    [LibraryImport(NativeCore.LibraryName, EntryPoint = "lr_avc_is_monochrome")]
    private static unsafe partial NativeStatus AvcNative(byte* data, ulong size, out uint result, ref NativeError error);
}
