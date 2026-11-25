using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Synect.Application.Ingest;

namespace Synect.Application.OData;

/// <summary>
/// Values of referenced records read for lookups (Catalog_Валюты, Catalog_Номенклатура, …): resource → ref → property → text.
/// Filled once per load (a whole resource read page by page with only the needed properties).
/// </summary>
public sealed class ODataLookupCache
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string?>>> _values = new(StringComparer.Ordinal);

    public bool Has(string resource) => _values.ContainsKey(resource);

    public void Add(string resource, string key, Dictionary<string, string?> properties)
    {
        if (!_values.TryGetValue(resource, out var byKey)) _values[resource] = byKey = new(StringComparer.OrdinalIgnoreCase);
        byKey[key] = properties;
    }

    public void MarkLoaded(string resource)
    {
        if (!_values.ContainsKey(resource)) _values[resource] = new(StringComparer.OrdinalIgnoreCase);
    }

    public string? Get(string resource, string key, IEnumerable<string> properties)
    {
        if (!_values.TryGetValue(resource, out var byKey) || !byKey.TryGetValue(key, out var values)) return null;
        foreach (var p in properties)
            if (values.TryGetValue(p, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }
}

/// <summary>
/// Turns one OData record into an item of the receiver (IngestSchema field names) by a profile source: ref, is_deleted,
/// data_version (kept in extra, compared on the next load), the mapped fields and the nested rows. Values are written the
/// way the extension writes them: amounts as decimal strings with a point, VAT rate as a number, currency as the letter
/// ISO code, empty strings, empty refs and empty dates as absent. Properties the record did not have are collected in
/// <see cref="MissingProperties"/> (a sign of a wrong name in the profile).
/// </summary>
public sealed partial class ODataRecordMapper(ODataProfile profile, ODataLookupCache lookups)
{
    public const string EmptyRef = "00000000-0000-0000-0000-000000000000";

    public HashSet<string> MissingProperties { get; } = new(StringComparer.Ordinal);

    public string? KeyOf(JsonElement record) => NormalizeRef(Get(record, profile.KeyProperty));

    /// <summary>TODO(verify-real): DataVersion is assumed to be a string ("AAAAAAAAAAE=") that changes on every write of the object.</summary>
    public string? VersionOf(JsonElement record) => Get(record, profile.VersionProperty) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public JsonObject Map(JsonElement record, ODataSource source)
    {
        var item = new JsonObject
        {
            ["ref"] = KeyOf(record),
            [IngestSchema.IsDeleted] = Get(record, profile.DeletionProperty) is { ValueKind: JsonValueKind.True },
        };
        if (VersionOf(record) is { } version) item["data_version"] = version;
        foreach (var (field, map) in source.Fields)
        {
            var value = Value(record, record, map, source.Resource.Name);
            if (value is not null) item[field] = value;
        }
        if (source.Children is { } children)
        {
            var rows = new JsonArray();
            var present = false;
            foreach (var table in children.Tables)
            {
                // TODO(verify-real): table parts are assumed to come inline as arrays of the document (no $select/$expand),
                // rows with LineNumber as a string ("1"), as in the documented examples.
                if (Get(record, table.Property.Name) is not { ValueKind: JsonValueKind.Array } lines)
                {
                    MissingProperties.Add($"{source.Resource.Name}.{table.Property.Name}");
                    continue;
                }
                present = true;
                foreach (var line in lines.EnumerateArray())
                {
                    var row = new JsonObject();
                    foreach (var (field, map) in table.Fields)
                    {
                        var value = Value(line, record, map, $"{source.Resource.Name}.{table.Property.Name}");
                        if (value is not null) row[field] = value;
                    }
                    rows.Add(row);
                }
            }
            // Line numbers restart in each table part; the receiver keeps them, so tables after the first are shifted.
            Renumber(rows);
            // A record without any of the tables keeps its stored rows (the receiver replaces rows only when they are sent).
            if (present) item[children.Name] = rows;
        }
        return item;
    }

    private static void Renumber(JsonArray rows)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i] is JsonObject o) o["line_no"] = i + 1;
    }

    private JsonNode? Value(JsonElement record, JsonElement header, ODataFieldMap map, string where)
    {
        if (map.Const is not null) return map.Const.DeepClone();
        if (map.SumOf is { } sums) return SumOf(header, sums);

        var raw = Get(record, map.Source!);
        if (raw is null)
        {
            MissingProperties.Add($"{where}.{map.Source}");
            return null;
        }
        if (map.OnlyType is { } type)
        {
            // TODO(verify-real): a composite reference is assumed to come as "{name}" (GUID) plus "{name}_Type" ("StandardODATA.Catalog_…").
            var actual = Get(record, map.Source + "_Type");
            if (actual is not { ValueKind: JsonValueKind.String } t || !string.Equals(t.GetString(), type, StringComparison.Ordinal)) return null;
        }
        if (IsEmpty(raw.Value) && map.Fallback is { } fallback) raw = Get(record, fallback);
        if (raw is null || IsEmpty(raw.Value)) return null;
        var value = raw.Value;

        string? looked = null;
        if (map.Lookup is { } lookup)
        {
            if (NormalizeRef(value) is not { } key) return null;
            if (map.Transform == "currency")
            {
                // The first property that is a currency the receiver knows (Description "USD", else Code "860"), else the first value.
                string? first = null;
                foreach (var p in lookup.Properties)
                {
                    if (lookups.Get(lookup.Resource, key, [p]) is not { } candidate) continue;
                    first ??= candidate;
                    if (TryCurrency(candidate) is { } code) return code;
                }
                return first;
            }
            looked = lookups.Get(lookup.Resource, key, lookup.Properties);
            if (looked is null) return null;
        }

        switch (map.Transform)
        {
            case "string":
            {
                var text = looked ?? Text(value);
                if (text is null) return null;
                if (map.MaxLength is { } max && text.Length > max) text = text[..max];
                return text;
            }
            case "ref":
                return NormalizeRef(value);
            case "date":
                return Date(looked ?? Text(value));
            case "money":
            case "decimal":
                return Number(looked is null ? value : JsonSerializer.SerializeToElement(looked)) is { } d ? Format(d) : null;
            case "int":
                return Number(value) is { } n && n == decimal.Truncate(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
            case "bool":
                return Bool(value);
            case "not":
                return Bool(value) is { } b ? !b : null;
            case "vat_rate":
                return VatRate(Text(value));
            case "enum":
            {
                var text = looked ?? Text(value);
                if (text is null) return null;
                return map.Map!.TryGetValue(text, out var mapped) ? mapped : map.MapDefault ?? text;
            }
            case "currency":
                return Currency(looked ?? Text(value));
            case "amount_with_vat":
            {
                if (Number(value) is not { } amount) return null;
                var included = map.HeaderFlag is { } flag && Get(header, flag) is { } f && Bool(f) == true;
                if (!included && map.VatSource is { } vatName && Get(record, vatName) is { } vatElement && Number(vatElement) is { } vat) amount += vat;
                return Format(amount);
            }
            default:
                throw new ODataProfileException($"Unknown transform '{map.Transform}'.");
        }
    }

    private static JsonNode? SumOf(JsonElement header, List<ODataSumOf> sums)
    {
        decimal total = 0;
        var any = false;
        foreach (var s in sums)
        {
            if (Get(header, s.Table) is not { ValueKind: JsonValueKind.Array } rows) continue;
            foreach (var row in rows.EnumerateArray())
                if (Get(row, s.Property) is { } v && Number(v) is { } d)
                {
                    total += d;
                    any = true;
                }
        }
        return any ? Format(total) : null;
    }

    public static JsonElement? Get(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var v) ? v : null;

    private static bool IsEmpty(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(e.GetString()) || e.GetString() == EmptyRef || e.GetString()!.StartsWith("0001-01-01", StringComparison.Ordinal),
        _ => false,
    };

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() is { } s && s.Trim() is { Length: > 0 } t ? t : null,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>A GUID in lower case; the empty reference and anything that is not a GUID give null.</summary>
    public static string? NormalizeRef(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.String } s && Guid.TryParse(s.GetString(), out var g) && g != Guid.Empty ? g.ToString("D") : null;

    /// <summary>1C dates come as local "2026-11-20T10:30:00" (no zone); 0001-01-01 is the empty date. Passed on as received.</summary>
    private static string? Date(string? text)
    {
        if (text is null || text.StartsWith("0001-01-01", StringComparison.Ordinal)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : null;
    }

    private static decimal? Number(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String && decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static bool? Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
        _ => null,
    };

    /// <summary>Decimal string with a point and no exponent, trailing zeros removed ("1234.5").</summary>
    private static string Format(decimal d) => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^НДС(\d{1,2})(_\d+)?$")]
    private static partial Regex VatEnum();

    /// <summary>
    /// 1C VAT rate enum names: НДС12 and НДС12_112 → "12", НДС0 → "0", БезНДС → "Без НДС" (stored as NULL by the receiver
    /// and kept in extra); a plain number ("6", 6.00, "6.0000") is written in the same canonical form as amounts ("6"), so
    /// the same rate always gives the same item and content hash; any other text is passed on. TODO(verify-real): enum names
    /// of the Uzbek configuration.
    /// </summary>
    public static string? VatRate(string? text)
    {
        if (text is null) return null;
        var m = VatEnum().Match(text);
        if (m.Success) return m.Groups[1].Value;
        if (text.Equals("БезНДС", StringComparison.OrdinalIgnoreCase)) return "Без НДС";
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)) return Format(d);
        return text;
    }

    /// <summary>The letter ISO code when the receiver recognises the value (letter code, numeric code, known name), else as received.</summary>
    public static string? Currency(string? text) => text is null ? null : TryCurrency(text) ?? text;

    private static string? TryCurrency(string text)
    {
        try
        {
            return IngestValues.Currency(JsonSerializer.SerializeToElement(text), "currency");
        }
        catch (IngestValueException)
        {
            return null;
        }
    }
}
