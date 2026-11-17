using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: NativeCoreChecks <native-core-library>");
    return 2;
}
var library = Path.GetFullPath(args[0]);
NativeLibrary.SetDllImportResolver(typeof(NativeCore).Assembly,
    (name, _, _) => name == NativeCore.LibraryName ? NativeLibrary.Load(library) : IntPtr.Zero);
int cases = 0, failures = 0;
void Check(bool value, string detail)
{
    cases++;
    if (!value)
    {
        failures++;
        Console.Error.WriteLine(detail);
    }
}
Check(NativeCore.GetAbiVersion() == NativeCore.AbiVersion, "ABI version mismatch");
Check(Unsafe.SizeOf<SourceFormat>() == 28 && Unsafe.SizeOf<OutputOptions>() == 32
    && Unsafe.SizeOf<ResolvedFormat>() == 24 && Unsafe.SizeOf<LumaRecode.Native.Hdr10PlusMetadata>() == 236,
    "Managed/native layout mismatch");
var baseline = new Transcoder(Path.Combine(Directory.GetCurrentDirectory(), "Assets"));
var samplingCases = new (string Name, Sampling Value)[]
{
    ("420", Sampling.Yuv420), ("422", Sampling.Yuv422), ("444", Sampling.Yuv444),
    ("gray", Sampling.Gray), ("440", Sampling.Yuv440), ("rgb", Sampling.Rgb),
    ("411", Sampling.Yuv411), ("410", Sampling.Yuv410)
};
var matrix =
    from depth in new[] { 8, 10, 12, 16 }
    from sampling in samplingCases
    from alpha in new[] { false, true }
    from purpose in new[] { Purpose.Share, Purpose.Archive }
    from codec in new[] { Codec.Hevc, Codec.Avc, Codec.Av1 }
    from dynamicRange in new[] { DynamicRange.Sdr, DynamicRange.Hdr }
    from requestedSampling in new[] { "auto", "source", "420", "422", "444", "gray" }
    select (depth, sampling, alpha, purpose, codec, dynamicRange, requestedSampling);
foreach (var (depth, sampling, alpha, purpose, codec, dynamicRange, requestedSampling) in matrix)
{
    var source = NativeCore.CreateSourceFormat();
    source.BitDepth = (uint)depth;
    source.Sampling = sampling.Value;
    source.DynamicRange = dynamicRange;
    source.ColorRange = ColorRange.Full;
    source.Flags = alpha ? SourceFlags.Alpha : SourceFlags.None;
    var options = NativeCore.CreateOutputOptions();
    options.Purpose = purpose;
    options.Codec = codec;
    options.DynamicRange = dynamicRange;
    options.Sampling = requestedSampling switch
    {
        "source" => Sampling.Source,
        "420" => Sampling.Yuv420,
        "422" => Sampling.Yuv422,
        "444" => Sampling.Yuv444,
        "gray" => Sampling.Gray,
        _ => Sampling.Auto
    };
    var name = sampling.Name == "rgb" ? $"gbrp{depth}le" : sampling.Name == "gray" ? $"gray{depth}le" : $"yuv{sampling.Name}p{depth}le";
    var pixel = new JsonObject
    {
        ["nb_components"] = sampling.Name == "gray" ? 1 : 3,
        ["flags"] = new JsonObject { ["rgb"] = sampling.Name == "rgb", ["alpha"] = alpha }
    };
    var media = new MediaInfo("synthetic.mkv", new JsonObject
    {
        ["pix_fmt"] = name,
        ["bits_per_raw_sample"] = depth,
        ["color_range"] = "pc"
    }, [], [], "sdr", null, "fixture", 1, [], pixel);
    var oldOptions = new EncodeOptions(Purpose: purpose == Purpose.Share ? "share" : "archive",
        Codec: codec switch
        {
            Codec.Avc => "avc",
            Codec.Av1 => "av1",
            _ => "hevc"
        },
        Output: dynamicRange == DynamicRange.Hdr ? "hdr" : "sdr", Sampling: requestedSampling);
    EncodeOptions? expected = null;
    try
    {
        expected = baseline.ResolveFormat(oldOptions, media);
    }
    catch (InvalidDataException) { }
    var status = NativeCore.ResolveFormat(in source, in options, out var actual, out var diagnostic);
    string detail = $"{depth}/{sampling.Name}/alpha={alpha}/{purpose}/{codec}/{dynamicRange}/{requestedSampling}";
    Check((expected is not null) == (status == NativeStatus.Ok), $"Policy acceptance differs: {detail}: {diagnostic}");
    if (expected is not null && status == NativeStatus.Ok)
    {
        var expectedSampling = expected.Sampling switch
        {
            "420" => Sampling.Yuv420,
            "422" => Sampling.Yuv422,
            "444" => Sampling.Yuv444,
            _ => Sampling.Gray
        };
        Check(actual.BitDepth == expected.Depth && actual.Sampling == expectedSampling
            && actual.ColorRange == (expected.ColorRange == "full" ? ColorRange.Full : ColorRange.Limited),
            $"Resolved format differs: {detail}");
    }
}
await HdrParityChecks.RunAsync(Check);
Console.WriteLine($"Managed/native policy and HDR parity: {cases} checks, {failures} failures.");
await ProcessChecks.RunAsync(Check, library);
OutputChecks.Run(Check);
MediaChecks.Run(Check);
await ProbeChecks.RunAsync(Check, library);
TimelineChecks.Run(Check);
PlanChecks.Run(Check);
Console.WriteLine($"Managed/native contracts total: {cases} checks, {failures} failures.");
return failures == 0 ? 0 : 1;
