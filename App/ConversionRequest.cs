using System.Text.Json.Serialization;

namespace VideoConverter;

public sealed record ConversionTools(string Ffmpeg, string Ffprobe);
public sealed record ConversionOptions(string Purpose, string Codec, string Output, string Transform,
    string? Profile, string Sampling, string Variant, string Audio, string Container, string SourceKind, bool CpuDecode,
    string ColorRange = "auto", string BitDepth = "auto");
public sealed record ConversionRequest(string Action, ConversionTools Tools, string Path, string? Output = null, ConversionOptions? Options = null);
internal sealed record HostErrorData(string Code, string Message);
internal sealed record HostError(int SchemaVersion, string Event, HostErrorData Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConversionRequest))]
[JsonSerializable(typeof(HostError))]
internal partial class ConversionJsonContext : JsonSerializerContext;
