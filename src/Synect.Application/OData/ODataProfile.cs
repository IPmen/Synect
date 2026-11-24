using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Synect.Application.Ingest;

namespace Synect.Application.OData;

/// <summary>A profile could not be read or does not fit the receiver's schema.</summary>
public sealed class ODataProfileException(string message) : InvalidOperationException(message);

/// <summary>A value of the profile (resource name, filter, property) with the mark "to be checked on a real application".</summary>
public sealed class ODataNamed
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("verify")] public bool Verify { get; set; }
}

public sealed class ODataFilter
{
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    [JsonPropertyName("verify")] public bool Verify { get; set; }
}

/// <summary>A lookup of a referenced record: Resource(guid'…') read once per run, its property taken.</summary>
public sealed class ODataLookup
{
    [JsonPropertyName("resource")] public string Resource { get; set; } = string.Empty;

    /// <summary>Properties tried in order; the first non-empty value is used.</summary>
    [JsonPropertyName("properties")] public List<string> Properties { get; set; } = [];
}

public sealed class ODataSumOf
{
    [JsonPropertyName("table")] public string Table { get; set; } = string.Empty;
    [JsonPropertyName("property")] public string Property { get; set; } = string.Empty;
}

/// <summary>
/// How one field of the receiver's item is made from an OData record. Transforms: string (text, empty = null), ref (GUID;
/// the empty ref = null), date (local date-time or date; 0001-01-01 = null), money (a number written as a decimal string
/// with a point), decimal, int, bool, not (negated bool), vat_rate (1C enum name НДС12 / НДС12_112 → "12", БезНДС →
/// "Без НДС"), enum (map), currency (letter ISO code, after an optional lookup), amount_with_vat (line amount with VAT:
/// the amount plus vat_source unless the header flag says VAT is included).
/// </summary>
public sealed class ODataFieldMap
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("const")] public JsonNode? Const { get; set; }
    [JsonPropertyName("transform")] public string Transform { get; set; } = "string";
    [JsonPropertyName("map")] public Dictionary<string, string>? Map { get; set; }
    [JsonPropertyName("map_default")] public string? MapDefault { get; set; }
    [JsonPropertyName("fallback")] public string? Fallback { get; set; }
    [JsonPropertyName("lookup")] public ODataLookup? Lookup { get; set; }

    /// <summary>Composite reference: the value is used only when "{source}_Type" equals this (e.g. StandardODATA.Catalog_Контрагенты).</summary>
    [JsonPropertyName("only_type")] public string? OnlyType { get; set; }

    [JsonPropertyName("sum_of")] public List<ODataSumOf>? SumOf { get; set; }
    [JsonPropertyName("vat_source")] public string? VatSource { get; set; }

    /// <summary>amount_with_vat: a property of the document header (e.g. СуммаВключаетНДС).</summary>
    [JsonPropertyName("header_flag")] public string? HeaderFlag { get; set; }

    /// <summary>Maximum length of a text value; longer text is cut (comment: 1000).</summary>
    [JsonPropertyName("max_length")] public int? MaxLength { get; set; }

    [JsonPropertyName("verify")] public bool Verify { get; set; }
}

public sealed class ODataChildTable
{
    [JsonPropertyName("property")] public ODataNamed Property { get; set; } = new();
    [JsonPropertyName("fields")] public Dictionary<string, ODataFieldMap> Fields { get; set; } = [];
}

public sealed class ODataChildren
{
    /// <summary>Name of the nested rows in the receiver's item: lines (documents) or settlements (bank documents).</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tables")] public List<ODataChildTable> Tables { get; set; } = [];
}

/// <summary>One OData resource feeding an entity of the receiver (documents come from two: sales and purchases).</summary>
public sealed class ODataSource
{
    [JsonPropertyName("resource")] public ODataNamed Resource { get; set; } = new();
    [JsonPropertyName("filter")] public ODataFilter? Filter { get; set; }

    /// <summary>
    /// Added to <see cref="Filter"/> only when the connection names its organization (settings.organization_key, a GUID):
    /// "{organization}" is replaced by guid'…'. Bases with several organizations need it.
    /// </summary>
    [JsonPropertyName("organization_filter")] public ODataFilter? OrganizationFilter { get; set; }

    /// <summary>Date property of documents: the delta reads documents dated within OData:DeltaWindowDays.</summary>
    [JsonPropertyName("date_property")] public ODataNamed? DateProperty { get; set; }

    [JsonPropertyName("fields")] public Dictionary<string, ODataFieldMap> Fields { get; set; } = [];
    [JsonPropertyName("children")] public ODataChildren? Children { get; set; }
}

public sealed class ODataEntityMap
{
    /// <summary>Entity of the receiver (IngestSchema): counterparties, contracts, bank_accounts, documents, bank_documents.</summary>
    [JsonPropertyName("entity")] public string Entity { get; set; } = string.Empty;

    [JsonPropertyName("sources")] public List<ODataSource> Sources { get; set; } = [];
}

/// <summary>
/// Mapping profile: for each entity of the receiver, the OData resources, property names, transforms and filters. It is data
/// (JSON in the repository, src/Synect.Application/OData/Profiles), not code: a profile can be replaced from
/// OData:ProfilesPath or per connection (settings.profile_json) without a rebuild. Standard properties every 1C object has
/// (Ref_Key, DataVersion, DeletionMark) are not listed.
/// </summary>
public sealed class ODataProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Standard property names (verify on a real application).</summary>
    [JsonPropertyName("key_property")] public string KeyProperty { get; set; } = "Ref_Key";

    [JsonPropertyName("version_property")] public string VersionProperty { get; set; } = "DataVersion";
    [JsonPropertyName("deletion_property")] public string DeletionProperty { get; set; } = "DeletionMark";

    [JsonPropertyName("entities")] public List<ODataEntityMap> Entities { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static ODataProfile Parse(string json, string origin)
    {
        ODataProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ODataProfile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ODataProfileException($"Profile {origin} is not valid JSON (line {ex.LineNumber}).");
        }
        if (profile is null) throw new ODataProfileException($"Profile {origin} is empty.");
        profile.Validate(origin);
        return profile;
    }

    /// <summary>Every entity exists in the receiver's schema, is keyed by ref, every field name is a field of it, every OData name is safe.</summary>
    public void Validate(string origin)
    {
        foreach (var name in new[] { KeyProperty, VersionProperty, DeletionProperty })
            if (!ODataUrl.IsName(name)) throw new ODataProfileException($"Profile {origin}: '{name}' is not a valid property name.");
        if (Entities.Count == 0) throw new ODataProfileException($"Profile {origin} has no entities.");
        foreach (var e in Entities)
        {
            var spec = IngestSchema.Find(e.Entity) ?? throw new ODataProfileException($"Profile {origin}: unknown entity '{e.Entity}'.");
            if (!spec.HasRef) throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}' is not keyed by ref.");
            if (e.Sources.Count == 0) throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}' has no sources.");
            var fields = spec.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var s in e.Sources)
            {
                if (!ODataUrl.IsName(s.Resource.Name)) throw new ODataProfileException($"Profile {origin}: '{s.Resource.Name}' is not a valid resource name.");
                foreach (var (field, map) in s.Fields)
                {
                    if (!fields.Contains(field) && field != "data_version")
                        throw new ODataProfileException($"Profile {origin}: '{field}' is not a field of {e.Entity}.");
                    CheckMap(origin, e.Entity, field, map);
                }
                if (s.Children is { } children)
                {
                    if (spec.Children is null || spec.Children.Name != children.Name)
                        throw new ODataProfileException($"Profile {origin}: {e.Entity} has no nested rows named '{children.Name}'.");
                    var childFields = spec.Children.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var t in children.Tables)
                    {
                        if (!ODataUrl.IsName(t.Property.Name)) throw new ODataProfileException($"Profile {origin}: '{t.Property.Name}' is not a valid table name.");
                        foreach (var (field, map) in t.Fields)
                        {
                            if (!childFields.Contains(field) && field is not ("item_ref" or "cost"))
                                throw new ODataProfileException($"Profile {origin}: '{field}' is not a field of {e.Entity}.{children.Name}.");
                            CheckMap(origin, e.Entity, field, map);
                        }
                    }
                }
            }
        }
    }

    private static readonly HashSet<string> Transforms =
        ["string", "ref", "date", "money", "decimal", "int", "bool", "not", "vat_rate", "enum", "currency", "amount_with_vat"];

    private static void CheckMap(string origin, string entity, string field, ODataFieldMap map)
    {
        if (!Transforms.Contains(map.Transform))
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} has an unknown transform '{map.Transform}'.");
        foreach (var n in new[] { map.Source, map.Fallback, map.VatSource, map.HeaderFlag }.OfType<string>())
            if (!ODataUrl.IsName(n)) throw new ODataProfileException($"Profile {origin}: {entity}.{field}: '{n}' is not a valid property name.");
        if (map.Source is null && map.Const is null && map.SumOf is null)
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} needs source, const or sum_of.");
        if (map.Lookup is { } l && (!ODataUrl.IsName(l.Resource) || l.Properties.Count == 0 || l.Properties.Any(p => !ODataUrl.IsName(p))))
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} has an invalid lookup.");
        if (map.Transform == "enum" && map.Map is null)
            throw new ODataProfileException($"Profile {origin}: {entity}.{field}: transform enum needs map.");
    }

    /// <summary>Profiles built into the assembly (Profiles/*.json as embedded resources), by name.</summary>
    public static IReadOnlyDictionary<string, string> BuiltIn { get; } = LoadBuiltIn();

    private static Dictionary<string, string> LoadBuiltIn()
    {
        var assembly = typeof(ODataProfile).Assembly;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".OData.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var short_ = name[(name.IndexOf(".OData.Profiles.", StringComparison.Ordinal) + ".OData.Profiles.".Length)..^".json".Length];
            result[short_] = text;
        }
        return result;
    }
}

/// <summary>
/// Picks the profile of a connection: settings.profile_json (a whole profile in the connection settings), else the
/// profile named settings.profile (or OData:DefaultProfile) from OData:ProfilesPath when that folder has it, else built in.
/// </summary>
public static class ODataProfileResolver
{
    public static ODataProfile Resolve(string? settingsJson, ODataOptions options)
    {
        string? name = null;
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("profile_json", out var inline) && inline.ValueKind == JsonValueKind.Object)
                    return ODataProfile.Parse(inline.GetRawText(), "of the connection settings");
                if (doc.RootElement.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String) name = p.GetString();
            }
        }
        return ByName(string.IsNullOrWhiteSpace(name) ? options.DefaultProfile : name!, options);
    }

    public static ODataProfile ByName(string name, ODataOptions options)
    {
        if (!ODataUrl.IsName(name.Replace("-", "_", StringComparison.Ordinal)))
            throw new ODataProfileException("A profile name has letters, digits, '-' and '_' only.");
        if (!string.IsNullOrWhiteSpace(options.ProfilesPath))
        {
            var path = Path.Combine(options.ProfilesPath, name + ".json");
            if (File.Exists(path)) return ODataProfile.Parse(File.ReadAllText(path), $"'{name}' ({options.ProfilesPath})");
        }
        return ODataProfile.BuiltIn.TryGetValue(name, out var json)
            ? ODataProfile.Parse(json, $"'{name}' (built in)")
            : throw new ODataProfileException($"Profile '{name}' not found; built in: {string.Join(", ", ODataProfile.BuiltIn.Keys)}.");
    }
}
