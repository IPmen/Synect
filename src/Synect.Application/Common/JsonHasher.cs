using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synect.Application.Common;

/// <summary>
/// Hashes JSON in canonical form (object keys sorted, no whitespace), so that a payload
/// re-sent with a different key order or formatting is treated as unchanged.
/// </summary>
public static class JsonHasher
{
    public static string Hash(string json)
    {
        var node = JsonNode.Parse(json);
        var canonical = Canonicalize(node)?.ToJsonString() ?? "null";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Hash of an arbitrary set of string parts; used to build synthetic ids.</summary>
    public static string HashParts(params string?[] parts)
    {
        var joined = string.Join("\u001f", parts.Select(p => p ?? string.Empty));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Canonicalize).ToArray()),
        JsonValue value => JsonNode.Parse(value.ToJsonString()),
        null => null,
        _ => throw new JsonException("Unsupported JSON node."),
    };
}
