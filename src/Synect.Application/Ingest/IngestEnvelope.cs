using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Synect.Application.Ingest;

/// <summary>A refused package: HTTP status, stable error code and our own message (never echoes the received content).</summary>
public sealed class IngestEnvelopeException(int httpStatus, string code, string message) : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;

    /// <summary>batch_id and sequence when they could be read, so that the refusal is journaled.</summary>
    public string? BatchId { get; init; }
    public int? Sequence { get; init; }
    public string? Entity { get; init; }
    public int? SchemaVersion { get; init; }
}

/// <summary>Envelope error codes.</summary>
public static class IngestEnvelopeCodes
{
    public const string InvalidJson = "invalid_json";
    public const string InvalidEnvelope = "invalid_envelope";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string UnknownEntity = "unknown_entity";
    public const string TooManyItems = "too_many_items";
    public const string PayloadTooLarge = "payload_too_large";
    public const string OrganizationMismatch = "organization_mismatch";
    public const string ConnectionDisabled = "connection_disabled";
    public const string BatchConflict = "batch_conflict";
}

/// <summary>The package envelope (all fields except items), parsed and checked.</summary>
public sealed record IngestEnvelope(
    int SchemaVersion,
    string? ExtensionVersion,
    string OrganizationTin,
    string BatchId,
    int Sequence,
    DateTimeOffset ExportedAt,
    string Mode,
    IngestEntitySpec Entity,
    bool IsLastBatch,
    string? FullSyncId,
    string? ExtraJson)
{
    public const string ModeFull = "full";
    public const string ModeDelta = "delta";

    public bool IsFull => Mode == ModeFull;
}

/// <summary>
/// Reads the envelope: schema_version, extension_version, organization_tin, batch_id, sequence, exported_at, mode (full,
/// delta), entity, items[], and for full exports full_sync_id and is_last_batch. Names are matched like item fields
/// (case, '_' and '-' ignored); unknown envelope fields are kept in ingest_batches.envelope_extra. schema_version is checked
/// first, so a newer contract gets a clear "unsupported_schema_version" whatever else changed.
/// </summary>
public static class IngestEnvelopeParser
{
    private static readonly string[] Known =
    [
        "schemaversion", "extensionversion", "organizationtin", "batchid", "sequence", "exportedat", "mode", "entity", "items",
        "islastbatch", "fullsyncid",
    ];

    public static (IngestEnvelope Envelope, JsonElement Items) Parse(JsonElement root, IngestOptions options, string? tenantTin)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "The package must be a JSON object.");

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var extra = new List<JsonProperty>();
        foreach (var p in root.EnumerateObject())
        {
            var name = IngestSchema.NormalizeName(p.Name);
            if (Known.Contains(name) && !fields.ContainsKey(name)) fields[name] = p.Value;
            else extra.Add(p);
        }

        string? batchId = null;
        int? sequence = null;
        string? entityName = null;
        try { batchId = Id(fields, "batchid", "batch_id", required: true); } catch (IngestEnvelopeException) { }
        try { sequence = Sequence(fields); } catch (IngestEnvelopeException) { }
        if (fields.TryGetValue("entity", out var en) && en.ValueKind == JsonValueKind.String) entityName = IngestSchema.Find(en.GetString())?.Name;

        int? version = null;
        try
        {
            version = SchemaVersion(fields);
            if (!options.AcceptedSchemaVersions.Contains(version.Value))
                throw new IngestEnvelopeException(400, IngestEnvelopeCodes.UnsupportedSchemaVersion,
                    $"schema_version {version} is not supported; supported: {string.Join(", ", options.AcceptedSchemaVersions)}.");

            var entity = IngestSchema.Find(fields.TryGetValue("entity", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                ?? throw new IngestEnvelopeException(400, IngestEnvelopeCodes.UnknownEntity,
                    $"'entity' must be one of: {string.Join(", ", IngestSchema.Entities.Select(x => x.Name))}.");

            var tin = fields.TryGetValue("organizationtin", out var t) && t.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? (t.ValueKind == JsonValueKind.String ? t.GetString()! : t.GetRawText()).Trim()
                : throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'organization_tin' is required.");
            if (tin.Length is not (9 or 14) || !tin.All(char.IsAsciiDigit))
                throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'organization_tin' must be 9 or 14 digits.");
            // The organization comes from the key; the package only confirms it. Nothing about other organizations is said.
            if (tenantTin is null || !string.Equals(tenantTin.Trim(), tin, StringComparison.Ordinal))
                throw new IngestEnvelopeException(403, IngestEnvelopeCodes.OrganizationMismatch,
                    "organization_tin does not match the organization of the ingest key.");

            var mode = fields.TryGetValue("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.Trim().ToLowerInvariant() : null;
            if (mode is not (IngestEnvelope.ModeFull or IngestEnvelope.ModeDelta))
                throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'mode' must be full or delta.");

            if (!fields.TryGetValue("exportedat", out var ex) || IngestValues.IsMissing(ex))
                throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'exported_at' is required.");
            DateTimeOffset exportedAt;
            try
            {
                exportedAt = IngestValues.Timestamp(ex, "exported_at", options.SourceUtcOffset)
                    ?? throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'exported_at' must be an ISO 8601 date-time.");
            }
            catch (IngestValueException v)
            {
                throw Bad(IngestEnvelopeCodes.InvalidEnvelope, v.Message.Replace("Field ", "", StringComparison.Ordinal));
            }

            var isLast = false;
            if (fields.TryGetValue("islastbatch", out var last) && !IngestValues.IsMissing(last))
            {
                try { isLast = IngestValues.Bool(last, "is_last_batch"); }
                catch (IngestValueException) { throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'is_last_batch' must be true or false."); }
            }
            var fullSyncId = Id(fields, "fullsyncid", "full_sync_id", required: mode == IngestEnvelope.ModeFull);

            if (!fields.TryGetValue("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'items' must be an array.");
            var count = items.GetArrayLength();
            if (count > options.MaxItems)
                throw new IngestEnvelopeException(413, IngestEnvelopeCodes.TooManyItems,
                    $"The package has {count} items; at most {options.MaxItems} are accepted (Ingest:MaxItems). Split it into smaller packages.");

            string? extension = null;
            if (fields.TryGetValue("extensionversion", out var ev) && !IngestValues.IsMissing(ev))
            {
                extension = ev.ValueKind == JsonValueKind.String ? ev.GetString()!.Trim() : ev.GetRawText();
                if (extension.Length > 64) extension = extension[..64];
            }

            var envelope = new IngestEnvelope(version.Value, extension, tin, batchId ?? Id(fields, "batchid", "batch_id", required: true)!,
                sequence ?? Sequence(fields), exportedAt, mode, entity, isLast && mode == IngestEnvelope.ModeFull, fullSyncId,
                extra.Count == 0 ? null : WriteObject(extra));
            return (envelope, items);
        }
        catch (IngestEnvelopeException ex) when (ex.BatchId is null)
        {
            throw new IngestEnvelopeException(ex.HttpStatus, ex.Code, ex.Message)
            {
                BatchId = batchId, Sequence = sequence, Entity = entityName, SchemaVersion = version,
            };
        }
    }

    private static int SchemaVersion(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("schemaversion", out var v) || IngestValues.IsMissing(v))
            throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'schema_version' is required.");
        // 1, "1" and "1.0" are the same version.
        if (IngestValues.TryDecimal(v, out var d) && d == decimal.Truncate(d) && d is > 0 and < 100_000) return (int)d;
        throw new IngestEnvelopeException(400, IngestEnvelopeCodes.UnsupportedSchemaVersion, "'schema_version' must be a whole number.");
    }

    private static int Sequence(Dictionary<string, JsonElement> fields)
    {
        if (fields.TryGetValue("sequence", out var s) && IngestValues.TryDecimal(s, out var d) && d == decimal.Truncate(d) && d is >= 1 and <= int.MaxValue)
            return (int)d;
        throw Bad(IngestEnvelopeCodes.InvalidEnvelope, "'sequence' must be a whole number from 1.");
    }

    private static string? Id(Dictionary<string, JsonElement> fields, string key, string name, bool required)
    {
        if (!fields.TryGetValue(key, out var v) || IngestValues.IsMissing(v))
            return required ? throw Bad(IngestEnvelopeCodes.InvalidEnvelope, $"'{name}' is required.") : null;
        var text = (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "").Trim();
        if (text.Length is 0 or > 64 || text.Any(char.IsControl))
            throw Bad(IngestEnvelopeCodes.InvalidEnvelope, $"'{name}' must be a text of 1 to 64 characters without control characters.");
        return text;
    }

    private static IngestEnvelopeException Bad(string code, string message) => new(400, code, message);

    private static string WriteObject(List<JsonProperty> properties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in properties)
                if (!IngestValues.HasUnstorableText(p.Value) && p.Name.IndexOf('\0') < 0) p.WriteTo(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
