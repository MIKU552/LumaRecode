using System.Runtime.InteropServices;

namespace LumaRecode.Native;

public enum NativeStatus : int
{
    Ok = 0, InvalidArgument = 1, Unsupported = 2, BufferTooSmall = 3, InvalidData = 4,
    Cancelled = 5, IoError = 6, InternalError = 7, TimedOut = 8, NotFound = 9, ResourceLimit = 10
}

// Assign every ABI value explicitly; inserting a managed member must not change
// the wire value of existing constants in the public C headers.
public enum Sampling : uint
{
    Auto = 0, Yuv420 = 1, Yuv422 = 2, Yuv444 = 3, Gray = 4, Rgb = 5, Yuv440 = 6, Yuv411 = 7, Yuv410 = 8, Source = 9
}
public enum ColorRange : uint
{
    Auto = 0, Limited = 1, Full = 2
}
public enum DynamicRange : uint
{
    Auto = 0, Sdr = 1, Hdr = 2
}
public enum Purpose : uint
{
    Share = 1, Archive = 2
}
public enum Codec : uint
{
    Hevc = 1, Avc = 2, Av1 = 3
}

[Flags]
public enum SourceFlags : uint
{
    None = 0, Alpha = 1, Raw = 2
}

/// <summary>Blittable source precision; public layout follows core.h, not managed serialization.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SourceFormat
{
    public uint StructSize;
    public uint AbiVersion;
    public uint BitDepth;
    public Sampling Sampling;
    public ColorRange ColorRange;
    public DynamicRange DynamicRange;
    public SourceFlags Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct OutputOptions
{
    public uint StructSize;
    public uint AbiVersion;
    public Purpose Purpose;
    public Codec Codec;
    public DynamicRange DynamicRange;
    public uint BitDepth;
    public Sampling Sampling;
    public ColorRange ColorRange;
}

[StructLayout(LayoutKind.Sequential)]
public struct ResolvedFormat
{
    public uint StructSize;
    public uint AbiVersion;
    public uint BitDepth;
    public Sampling Sampling;
    public ColorRange ColorRange;
    public DynamicRange DynamicRange;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct Hdr10PlusMetadata
{
    public uint StructSize;
    public uint AbiVersion;
    public uint ApplicationVersion;
    public uint NumberOfWindows;
    public uint TargetedDisplayLuminance;
    public fixed uint MaxScl[3];
    public uint AverageRgb;
    public uint DistributionCount;
    public fixed uint DistributionIndices[15];
    public fixed uint DistributionValues[15];
    public uint ToneMapping;
    public uint KneePointX;
    public uint KneePointY;
    public uint AnchorCount;
    public fixed uint Anchors[15];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeError
{
    public uint StructSize;
    public NativeStatus Code;
    public fixed byte Message[768];
}
