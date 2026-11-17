using System.Runtime.InteropServices;

namespace LumaRecode.Native;

public enum NativeStatus : int
{
    Ok, InvalidArgument, Unsupported, BufferTooSmall, InvalidData,
    Cancelled, IoError, InternalError, TimedOut, NotFound
}

public enum Sampling : uint { Auto, Yuv420, Yuv422, Yuv444, Gray, Rgb, Yuv440, Yuv411, Yuv410, Source }
public enum ColorRange : uint { Auto, Limited, Full }
public enum DynamicRange : uint { Auto, Sdr, Hdr }
public enum Purpose : uint { Share = 1, Archive }
public enum Codec : uint { Hevc = 1, Avc, Av1 }

[Flags]
public enum SourceFlags : uint { None, Alpha = 1, Raw = 2 }

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
