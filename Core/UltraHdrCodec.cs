using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoConverter.Core;

public sealed record UltraHdrInfo(int Width, int Height, int GainMapWidth, int GainMapHeight);

/// <summary>Lazy, AOT-safe bindings to Google's libultrahdr JPEG encoder and probe.</summary>
/// <remarks>The bundled library writes both XMP and ISO 21496-1 metadata. JPEG
/// compression and gain-map generation use the upstream implementation.</remarks>
public static unsafe partial class UltraHdrCodec
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Error
    {
        public int Code, HasDetail;
        public fixed byte Detail[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawImage
    {
        public int Format, Gamut, Transfer, Range;
        public uint Width, Height;
        public nint Plane0, Plane1, Plane2;
        public uint Stride0, Stride1, Stride2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompressedImage
    {
        public nint Data;
        public nuint Size, Capacity;
        public int Gamut, Transfer, Range;
    }

    [LibraryImport("uhdr", EntryPoint = "uhdr_create_encoder")]
    private static partial nint CreateEncoder();
    [LibraryImport("uhdr", EntryPoint = "uhdr_release_encoder")]
    private static partial void ReleaseEncoder(nint encoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_enc_set_raw_image")]
    private static partial Error SetRawImage(nint encoder, ref RawImage image, int intent);
    [LibraryImport("uhdr", EntryPoint = "uhdr_enc_set_quality")]
    private static partial Error SetQuality(nint encoder, int quality, int intent);
    [LibraryImport("uhdr", EntryPoint = "uhdr_enc_set_using_multi_channel_gainmap")]
    private static partial Error SetMultiChannel(nint encoder, int enabled);
    [LibraryImport("uhdr", EntryPoint = "uhdr_enc_set_gainmap_scale_factor")]
    private static partial Error SetGainMapScale(nint encoder, int scale);
    [LibraryImport("uhdr", EntryPoint = "uhdr_enc_set_preset")]
    private static partial Error SetPreset(nint encoder, int preset);
    [LibraryImport("uhdr", EntryPoint = "uhdr_encode")]
    private static partial Error EncodeNative(nint encoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_get_encoded_stream")]
    private static partial nint GetEncoded(nint encoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_create_decoder")]
    private static partial nint CreateDecoder();
    [LibraryImport("uhdr", EntryPoint = "uhdr_release_decoder")]
    private static partial void ReleaseDecoder(nint decoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_set_image")]
    private static partial Error SetCompressedImage(nint decoder, ref CompressedImage image);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_probe")]
    private static partial Error ProbeNative(nint decoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_get_image_width")]
    private static partial int GetWidth(nint decoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_get_image_height")]
    private static partial int GetHeight(nint decoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_get_gainmap_width")]
    private static partial int GetGainMapWidth(nint decoder);
    [LibraryImport("uhdr", EntryPoint = "uhdr_dec_get_gainmap_height")]
    private static partial int GetGainMapHeight(nint decoder);

    private static void Check(Error error)
    {
        if (error.Code == 0)
        {
            return;
        }
        var detail = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref error, 1)).Slice(8, 256);
        int end = detail.IndexOf((byte)0);
        throw new InvalidDataException("Ultra HDR 编码组件：" + Encoding.UTF8.GetString(end < 0 ? detail : detail[..end]));
    }

    /// <summary>Encodes full-range BT.2020 HLG/PQ packed RGB10 pixels (FFmpeg x2bgr10le).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, bool hlg, int quality = 95, CancellationToken cancellation = default)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4) || quality is < 1 or > 100)
        {
            throw new InvalidDataException("Ultra HDR 图像尺寸或像素缓冲区无效。");
        }
        cancellation.ThrowIfCancellationRequested();
        nint encoder = CreateEncoder();
        if (encoder == 0)
        {
            throw new OutOfMemoryException();
        }
        try
        {
            fixed (byte* pointer = pixels)
            {
                var image = new RawImage
                {
                    Format = 5,
                    Gamut = 2,
                    Transfer = hlg ? 1 : 2,
                    Range = 1,
                    Width = (uint)width,
                    Height = (uint)height,
                    Plane0 = (nint)pointer,
                    Stride0 = (uint)width
                };
                Check(SetRawImage(encoder, ref image, 0));
                Check(SetQuality(encoder, quality, 2));
                Check(SetQuality(encoder, quality, 3));
                Check(SetMultiChannel(encoder, 1));
                Check(SetGainMapScale(encoder, 4));
                Check(SetPreset(encoder, 1));
                // Native compression is bounded by one frame. Cancellation is
                // checked again before the caller can publish the result.
                Check(EncodeNative(encoder));
                cancellation.ThrowIfCancellationRequested();
                nint stream = GetEncoded(encoder);
                if (stream == 0)
                {
                    throw new InvalidDataException("Ultra HDR 编码结果无效。");
                }
                var encoded = *(CompressedImage*)stream;
                if (encoded.Data == 0 || encoded.Size == 0 || encoded.Size > int.MaxValue)
                {
                    throw new InvalidDataException("Ultra HDR 编码结果无效。");
                }
                return new ReadOnlySpan<byte>((void*)encoded.Data, checked((int)encoded.Size)).ToArray();
            }
        }
        finally
        {
            ReleaseEncoder(encoder);
        }
    }

    /// <summary>Reads an FFmpeg frame through a mapped view instead of allocating a full-resolution managed copy.</summary>
    public static byte[] EncodeFile(string path, int width, int height, bool hlg, int quality = 95, CancellationToken cancellation = default)
    {
        int length = checked(width * height * 4);
        if (width <= 0 || height <= 0 || new FileInfo(path).Length != length)
        {
            throw new InvalidDataException("Ultra HDR 图像尺寸或像素缓冲区无效。");
        }
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            return Encode(new ReadOnlySpan<byte>(pointer + view.PointerOffset, length), width, height, hlg, quality, cancellation);
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    /// <summary>Validates the primary/gain-map pair with the official decoder.</summary>
    public static UltraHdrInfo Inspect(ReadOnlySpan<byte> jpeg)
    {
        nint decoder = CreateDecoder();
        if (decoder == 0)
        {
            throw new OutOfMemoryException();
        }
        try
        {
            fixed (byte* pointer = jpeg)
            {
                var image = new CompressedImage
                {
                    Data = (nint)pointer,
                    Size = (nuint)jpeg.Length,
                    Capacity = (nuint)jpeg.Length,
                    Gamut = -1,
                    Transfer = -1,
                    Range = -1
                };
                Check(SetCompressedImage(decoder, ref image));
                Check(ProbeNative(decoder));
                var info = new UltraHdrInfo(GetWidth(decoder), GetHeight(decoder), GetGainMapWidth(decoder), GetGainMapHeight(decoder));
                if (info.Width <= 0 || info.Height <= 0 || info.GainMapWidth <= 0 || info.GainMapHeight <= 0)
                {
                    throw new InvalidDataException("Ultra HDR 主图或增益图无效。");
                }
                return info;
            }
        }
        finally
        {
            ReleaseDecoder(decoder);
        }
    }
}
