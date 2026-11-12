using System.Text.Json.Serialization;

namespace VideoConverter;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(int[]))]
internal partial class AppJsonContext : JsonSerializerContext;
