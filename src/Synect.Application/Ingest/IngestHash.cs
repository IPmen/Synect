using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Synect.Application.Ingest;

/// <summary>
/// Content hash of an item: SHA-256 (hex) of its JSON with object keys sorted and without whitespace, so that the same item
/// sent with another key order or formatting is "unchanged". Scalars are hashed as written ("1.0" and "1.00" differ: such
/// an item is simply applied again). Written straight from the parsed element, without building a JSON tree.
/// </summary>
public static class IngestHash
{
    public static string Compute(JsonElement item)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
            Write(writer, item);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexStringLower(hash);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (var p in element.EnumerateObject()) properties.Add(p);
                properties.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var p in properties)
                {
                    writer.WritePropertyName(p.Name);
                    Write(writer, p.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var x in element.EnumerateArray()) Write(writer, x);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
