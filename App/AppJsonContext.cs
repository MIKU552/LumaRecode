using System.Text.Json.Serialization;

namespace VideoConverter;

// Settings and translation resources use generated metadata in both AOT and JIT builds.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJsonContext : JsonSerializerContext;
