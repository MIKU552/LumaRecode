using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VideoConverter.Core;

// Source generation keeps CLI/event serialization available after Native AOT trimming.
[JsonSerializable(typeof(JsonNode))]
internal partial class BackendJsonContext : JsonSerializerContext;

/// <summary>Serializes JSON documents and event lines with a reflection-free shared context.</summary>
public static class BackendJson
{
    private static readonly BackendJsonContext pretty = new(new JsonSerializerOptions { WriteIndented = true });
    public static string Write(JsonNode? node, bool indented = false) =>
        JsonSerializer.Serialize(node, indented ? pretty.JsonNode : BackendJsonContext.Default.JsonNode);
}
