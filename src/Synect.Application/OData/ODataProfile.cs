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

    /// <summary>
    /// Several candidate properties instead of <see cref="Source"/>: the first present, non-empty one (with
    /// <see cref="OnlyType"/>: the first whose "{name}_Type" matches). For subconto of an accounting register, whose order
    /// depends on the account: ExtDimension1, ExtDimension2, ExtDimension3.
    /// </summary>
    [JsonPropertyName("source_any")] public List<string>? SourceAny { get; set; }

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

/// <summary>
/// A parameter of a virtual table. type "condition" (default): a filter expression passed as a quoted string, with the
/// placeholders {organization} (guid'…' of settings.organization_key; without it the parameter is left out) and {accounts}
/// (see <see cref="ODataAccounts"/>). type "datetime": only the placeholders {today_start} or {today_end} (local date of
/// the application).
/// </summary>
public sealed class ODataParameter
{
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "condition";
    [JsonPropertyName("verify")] public bool Verify { get; set; }
}

/// <summary>
/// Accounts of a chart of accounts by code, for the {accounts} placeholder: "Account_Key eq guid'…' or …". A code ending in
/// '*' takes every account whose code starts with the rest (e.g. "29*"). Codes are matched against the Code property of
/// <see cref="Chart"/>, read once per load.
/// </summary>
public sealed class ODataAccounts
{
    [JsonPropertyName("chart")] public string Chart { get; set; } = string.Empty;
    [JsonPropertyName("property")] public string Property { get; set; } = "Account_Key";
    [JsonPropertyName("codes")] public List<string> Codes { get; set; } = [];
    [JsonPropertyName("verify")] public bool Verify { get; set; }
}

/// <summary>One OData resource feeding an entity of the receiver (documents come from two: sales and purchases).</summary>
public sealed class ODataSource
{
    [JsonPropertyName("resource")] public ODataNamed Resource { get; set; } = new();

    /// <summary>Snapshot entities: the virtual table of the register (Balance, SliceLast, …).</summary>
    [JsonPropertyName("virtual_table")] public ODataNamed? VirtualTable { get; set; }

    /// <summary>Parameters of <see cref="VirtualTable"/> by name (Period, Condition, AccountCondition, …).</summary>
    [JsonPropertyName("parameters")] public Dictionary<string, ODataParameter>? Parameters { get; set; }

    [JsonPropertyName("accounts")] public ODataAccounts? Accounts { get; set; }

    /// <summary>Snapshot entities: $orderby for paging (properties of the virtual table); none by default.</summary>
    [JsonPropertyName("order_by")] public string? OrderBy { get; set; }

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

    /// <summary>
    /// The client may not publish it: when a resource of the entity answers 404, or none of its accounts exists, the entity
    /// is skipped with a warning (the load goes on and stays complete) and it is not counted by the overdue rule until it has
    /// been loaded at least once.
    /// </summary>
    [JsonPropertyName("optional")] public bool Optional { get; set; }

    /// <summary>
    /// A register read as a whole: rows of a virtual table (no Ref_Key, no DataVersion), grouped by the receiver's key with
    /// numeric fields summed, sent as a full export every load (current-state tables: absent rows removed; dated balances:
    /// the date of the load replaced). For entities not keyed by ref (stock, prices, settlement_balances, bank_account_balances).
    /// </summary>
    [JsonPropertyName("snapshot")] public bool Snapshot { get; set; }

    /// <summary>
    /// Snapshot: rows with the same receiver key. "sum" (default): numeric fields added (balances by batch, by settlement
    /// document). "first": the first row is kept and the others counted in a warning (prices by an extra dimension).
    /// </summary>
    [JsonPropertyName("duplicates")] public string Duplicates { get; set; } = "sum";

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
            if (e.Snapshot && spec.HasRef)
                throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}' is keyed by ref; a snapshot is for balances, stock and prices.");
            if (!e.Snapshot && !spec.HasRef)
                throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}' is not keyed by ref; read it as a snapshot of a register.");
            if (e.Sources.Count == 0) throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}' has no sources.");
            if (e.Duplicates is not ("sum" or "first"))
                throw new ODataProfileException($"Profile {origin}: entity '{e.Entity}': duplicates is sum or first.");
            var fields = spec.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var s in e.Sources)
            {
                if (!ODataUrl.IsName(s.Resource.Name)) throw new ODataProfileException($"Profile {origin}: '{s.Resource.Name}' is not a valid resource name.");
                CheckSnapshotSource(origin, e, s);
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

    public static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal) { "{organization}", "{accounts}" };

    private static void CheckSnapshotSource(string origin, ODataEntityMap e, ODataSource s)
    {
        var where = $"Profile {origin}: {e.Entity} ({s.Resource.Name})";
        if (!e.Snapshot)
        {
            if (s.VirtualTable is not null || s.Parameters is not null || s.Accounts is not null || s.OrderBy is not null)
                throw new ODataProfileException($"{where}: virtual_table, parameters, accounts and order_by belong to a snapshot entity.");
            return;
        }
        if (s.Children is not null || s.DateProperty is not null)
            throw new ODataProfileException($"{where}: a snapshot has neither children nor date_property.");
        if (s.VirtualTable is { } vt && !ODataUrl.VirtualTables.Contains(vt.Name))
            throw new ODataProfileException($"{where}: '{vt.Name}' is not a supported virtual table ({string.Join(", ", ODataUrl.VirtualTables)}).");
        if (s.Parameters is { Count: > 0 } && s.VirtualTable is null)
            throw new ODataProfileException($"{where}: parameters need a virtual_table.");
        foreach (var (name, p) in s.Parameters ?? [])
        {
            if (!ODataUrl.IsName(name)) throw new ODataProfileException($"{where}: '{name}' is not a valid parameter name.");
            switch (p.Type)
            {
                case "datetime" when p.Value is "{today_start}" or "{today_end}":
                    break;
                case "datetime":
                    throw new ODataProfileException($"{where}: parameter {name} of type datetime is {{today_start}} or {{today_end}}.");
                case "condition":
                    if (string.IsNullOrWhiteSpace(p.Value)) throw new ODataProfileException($"{where}: parameter {name} is empty.");
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(p.Value, @"\{[^}]*\}"))
                        if (!Placeholders.Contains(m.Value)) throw new ODataProfileException($"{where}: parameter {name}: unknown placeholder {m.Value}.");
                    if (p.Value.Contains("{accounts}", StringComparison.Ordinal) && s.Accounts is null)
                        throw new ODataProfileException($"{where}: parameter {name} uses {{accounts}} without accounts.");
                    break;
                default:
                    throw new ODataProfileException($"{where}: parameter {name} has an unknown type '{p.Type}'.");
            }
        }
        if (s.Accounts is { } a)
        {
            if (!ODataUrl.IsName(a.Chart) || !ODataUrl.IsName(a.Property) || a.Codes.Count == 0
                || a.Codes.Any(c => string.IsNullOrWhiteSpace(c) || c.TrimEnd('*').Length == 0 || c.IndexOf('*') is >= 0 and var i && i != c.Length - 1))
                throw new ODataProfileException($"{where}: accounts need a chart, a property and codes (a '*' only at the end).");
        }
        if (s.OrderBy is { } order)
            foreach (var o in order.Split(','))
                if (!ODataUrl.IsName(o.Trim().Split(' ')[0])) throw new ODataProfileException($"{where}: order_by '{order}' is not valid.");
    }

    private static void CheckMap(string origin, string entity, string field, ODataFieldMap map)
    {
        if (!Transforms.Contains(map.Transform))
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} has an unknown transform '{map.Transform}'.");
        foreach (var n in new[] { map.Source, map.Fallback, map.VatSource, map.HeaderFlag }.OfType<string>().Concat(map.SourceAny ?? []))
            if (!ODataUrl.IsName(n)) throw new ODataProfileException($"Profile {origin}: {entity}.{field}: '{n}' is not a valid property name.");
        if (map.Source is not null && map.SourceAny is not null)
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} has both source and source_any.");
        if (map.Source is null && map.SourceAny is not { Count: > 0 } && map.Const is null && map.SumOf is null)
            throw new ODataProfileException($"Profile {origin}: {entity}.{field} needs source, source_any, const or sum_of.");
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
