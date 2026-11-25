using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace LumaRecode.Native;

/// <summary>Thin generated C ABI imports. All format policy remains in the native core.</summary>
/// <remarks>Hosts deploy their architecture's core beside the app and dispatch long work off the UI thread.</remarks>
public static partial class NativeCore
{
    public const string LibraryName = "LumaRecodeCore";
    public const uint AbiVersion = 1;

    [LibraryImport(LibraryName, EntryPoint = "lr_abi_version")]
    public static partial uint GetAbiVersion();

    public static SourceFormat CreateSourceFormat()
    {
        SourceFormat value = default;
        Check(InitializeSource(ref value, (ulong)Unsafe.SizeOf<SourceFormat>()));
        return value;
    }

    public static NativeStatus GetFormatChoices(in SourceFormat source, Purpose purpose, Codec codec, DynamicRange range,
        out FormatChoices choices, out string diagnostic)
    {
        choices = default;
        Check(InitializeChoices(ref choices, (ulong)Unsafe.SizeOf<FormatChoices>()));
        var error = CreateError();
        var status = ReadChoices(in source, purpose, codec, range, ref choices, ref error);
        diagnostic = ReadDiagnostic(in error);
        return status;
    }

    [LibraryImport(LibraryName, EntryPoint = "lr_format_choices_init")]
    private static partial NativeStatus InitializeChoices(ref FormatChoices value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_get_format_choices")]
    private static partial NativeStatus ReadChoices(in SourceFormat source, Purpose purpose, Codec codec, DynamicRange range,
        ref FormatChoices value, ref NativeError error);

    public static OutputOptions CreateOutputOptions()
    {
        OutputOptions value = default;
        Check(InitializeOptions(ref value, (ulong)Unsafe.SizeOf<OutputOptions>()));
        return value;
    }

    public static NativeStatus ResolveFormat(in SourceFormat source, in OutputOptions options,
        out ResolvedFormat result, out string diagnostic)
    {
        result = default;
        Check(InitializeResult(ref result, (ulong)Unsafe.SizeOf<ResolvedFormat>()));
        NativeError error = default;
        Check(InitializeError(ref error, (ulong)Unsafe.SizeOf<NativeError>()));
        var status = Resolve(in source, in options, ref result, ref error);
        diagnostic = ReadDiagnostic(in error);
        return status;
    }

    public static unsafe NativeStatus ParseHdr10Plus(ReadOnlySpan<byte> payload,
        out Hdr10PlusMetadata metadata, out string diagnostic)
    {
        metadata = default;
        Check(InitializeMetadata(ref metadata, (ulong)sizeof(Hdr10PlusMetadata)));
        NativeError error = default;
        Check(InitializeError(ref error, (ulong)sizeof(NativeError)));
        fixed (byte* bytes = payload)
        {
            var status = ParseMetadata(bytes, (ulong)payload.Length, ref metadata, ref error);
            diagnostic = ReadDiagnostic(in error);
            return status;
        }
    }

    internal static NativeError CreateError()
    {
        NativeError value = default;
        Check(InitializeError(ref value, (ulong)Unsafe.SizeOf<NativeError>()));
        return value;
    }

    internal static unsafe string ReadDiagnostic(in NativeError error)
    {
        fixed (byte* message = error.Message)
        {
            int length = 0;
            while (length < 768 && message[length] != 0)
            {
                length++;
            }
            return Encoding.UTF8.GetString(message, length);
        }
    }

    internal static void Check(NativeStatus status)
    {
        if (status != NativeStatus.Ok)
        {
            throw new InvalidOperationException($"Native core call failed: {status}.");
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "lr_source_format_init")]
    private static partial NativeStatus InitializeSource(ref SourceFormat value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_encode_options_init")]
    private static partial NativeStatus InitializeOptions(ref OutputOptions value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_resolved_format_init")]
    private static partial NativeStatus InitializeResult(ref ResolvedFormat value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_error_init")]
    private static partial NativeStatus InitializeError(ref NativeError value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_hdr10plus_metadata_init")]
    private static partial NativeStatus InitializeMetadata(ref Hdr10PlusMetadata value, ulong capacity);
    [LibraryImport(LibraryName, EntryPoint = "lr_resolve_format")]
    private static partial NativeStatus Resolve(in SourceFormat source, in OutputOptions options,
        ref ResolvedFormat result, ref NativeError error);
    [LibraryImport(LibraryName, EntryPoint = "lr_hdr10plus_parse_t35")]
    private static unsafe partial NativeStatus ParseMetadata(byte* bytes, ulong length,
        ref Hdr10PlusMetadata metadata, ref NativeError error);
}
