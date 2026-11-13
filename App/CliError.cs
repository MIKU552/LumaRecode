using System.Text.Json.Serialization;

namespace VideoConverter;

// The extraction launcher cannot depend on the application payload to report
// startup failures. Keep this small CLI envelope shared by source, not by DLL.
internal sealed record HostErrorData(string Code, string Message);
internal sealed record HostError(int SchemaVersion, string Event, HostErrorData Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HostError))]
internal partial class CliErrorJsonContext : JsonSerializerContext;
