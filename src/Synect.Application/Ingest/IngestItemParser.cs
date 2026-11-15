using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Synect.Application.Ingest;

/// <summary>One item of a package after parsing: column values, unknown fields, key and change time, or the reason it was refused.</summary>
public sealed class ParsedItem
{
    public int Index { get; init; }

    /// <summary>Key of the record (ref, or the parts of a balance key joined with U+001F); null when it could not be read.</summary>
    public string? Key { get; set; }

    /// <summary>The item's ref (or key) for the answer.</summary>
    public string? DisplayKey { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsValid => ErrorCode is null;

    /// <summary>Column -> value (string, string[], long, int, decimal, bool, DateOnly, DateTimeOffset or null).</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields Synect does not know, as a JSON object.</summary>
    public string ExtraJson { get; set; } = "{}";

    public DateTimeOffset ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>is_deleted = true with required fields missing: only the deletion of a known record can be applied.</summary>
    public bool IsDeletionNotice { get; set; }

    /// <summary>Nested rows (lines, settlements); null when the item does not carry the field (stored rows are kept).</summary>
    public List<Dictionary<string, object?>>? Children { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The item exactly as received.</summary>
    public string RawJson { get; set; } = "{}";
}

/// <summary>Parses items of one entity by <see cref="IngestSchema"/>. Errors are per item and never stop the package.</summary>
public sealed class IngestItemParser
{
    public const string KeySeparator = "\u001f";

    private readonly IngestEntitySpec _spec;
    private readonly IngestOptions _options;
    private readonly Dictionary<string, IngestField> _fields;
    private readonly Dictionary<string, IngestField>? _childFields;
    private readonly HashSet<string> _childNames = new(StringComparer.Ordinal);

    public IngestItemParser(IngestEntitySpec spec, IngestOptions options)
    {
        _spec = spec;
        _options = options;
        _fields = Index(spec.Fields);
        if (spec.Children is { } children)
        {
            _childFields = Index(children.Fields);
            foreach (var n in children.Aliases.Prepend(children.Name)) _childNames.Add(IngestSchema.NormalizeName(n));
        }
    }

    private static Dictionary<string, IngestField> Index(IEnumerable<IngestField> fields)
    {
        var map = new Dictionary<string, IngestField>(StringComparer.Ordinal);
        foreach (var f in fields)
            foreach (var n in f.Aliases.Prepend(f.Name))
                map.TryAdd(IngestSchema.NormalizeName(n), f);
        return map;
    }

    public ParsedItem Parse(int index, JsonElement item, DateTimeOffset exportedAt)
    {
        var result = new ParsedItem { Index = index };
        try
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new IngestValueException(IngestCodes.InvalidItem, "The item must be a JSON object.");
            result.RawJson = item.GetRawText();
            // The key first, so that even a refused item can be named in the answer and kept in the raw layer.
            TryReadKey(item, result);
            if (IngestValues.HasUnstorableText(item))
                throw new IngestValueException(IngestCodes.InvalidText, "The item contains NUL characters or invalid UTF-16 text.");

            var present = new HashSet<string>(StringComparer.Ordinal);
            var extra = new List<JsonProperty>();
            JsonElement? children = null;
            foreach (var p in item.EnumerateObject())
            {
                var name = IngestSchema.NormalizeName(p.Name);
                if (_fields.TryGetValue(name, out var field) && present.Add(field.Name))
                {
                    if (!IngestValues.IsMissing(p.Value)) Read(field, p.Value, result.Values, extra, p);
                    continue;
                }
                if (_childNames.Contains(name) && children is null)
                {
                    children = p.Value;
                    continue;
                }
                extra.Add(p);
            }

            result.IsDeleted = result.Values.TryGetValue(IngestSchema.IsDeleted, out var d) && d is true;
            result.ModifiedAt = result.Values.TryGetValue(IngestSchema.ModifiedAt, out var m) && m is DateTimeOffset at ? at : exportedAt;
            foreach (var f in _spec.Fields.Where(f => f.Type == IngestFieldType.Currency && !result.Values.ContainsKey(f.Column)))
                result.Values[f.Column] = _options.DefaultCurrency;
            if (_spec.Fields.Any(f => f.Column == "posted") && !result.Values.ContainsKey("posted") && !_spec.Fields.Single(f => f.Column == "posted").Required)
                result.Values["posted"] = false;
            foreach (var f in _spec.Fields.Where(f => f.Default != IngestFieldDefault.None && !result.Values.ContainsKey(f.Column)))
                result.Values[f.Column] = f.Default == IngestFieldDefault.False ? false : exportedAt;

            var missingKeys = _spec.Keys.Where(k => k.Required && !result.Values.ContainsKey(k.Column)).ToList();
            if (_spec.HasRef && missingKeys.Count > 0)
                throw new IngestValueException(IngestCodes.EmptyRef, "Field 'ref' is missing or empty.");
            if (missingKeys.Count > 0)
                throw new IngestValueException(IngestCodes.MissingField, $"Required key field(s) missing: {string.Join(", ", missingKeys.Select(k => k.Name))}.");
            result.Key = BuildKey(result.Values);
            result.DisplayKey ??= result.Key.Replace(KeySeparator, "|", StringComparison.Ordinal);

            var missing = _spec.Fields.Where(f => f.Required && !result.Values.ContainsKey(f.Column)).Select(f => f.Name).ToList();
            if (missing.Count > 0)
            {
                // A deletion of an object that 1C no longer has may carry only its ref (current-state rows: only their key).
                if (!(result.IsDeleted && (_spec.HasRef || _spec.CurrentState)))
                    throw new IngestValueException(IngestCodes.MissingField, $"Required field(s) missing: {string.Join(", ", missing)}.");
                result.IsDeletionNotice = true;
            }

            if (children is { } rows && _childFields is not null && !IngestValues.IsMissing(rows))
                result.Children = ReadChildren(rows);
            else if (children is not null)
                result.Children = [];

            if (_spec.SearchColumn is { } search)
                result.Values[search] = Synect.Domain.Counterparties.CounterpartyNameNormalizer.NormalizeText(
                    result.Values.GetValueOrDefault("name") as string);

            result.ExtraJson = WriteObject(extra);
            result.ContentHash = IngestHash.Compute(item);
        }
        catch (IngestValueException ex)
        {
            result.ErrorCode = ex.Code;
            result.ErrorMessage = ex.Message;
        }
        return result;
    }

    private void TryReadKey(JsonElement item, ParsedItem result)
    {
        try
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in item.EnumerateObject())
                if (_fields.TryGetValue(IngestSchema.NormalizeName(p.Name), out var f) && _spec.KeyFields.Contains(f.Name)
                    && !values.ContainsKey(f.Column) && !IngestValues.IsMissing(p.Value))
                    Read(f, p.Value, values, [], p);
            foreach (var k in _spec.Keys.Where(k => k.Type == IngestFieldType.Currency && !values.ContainsKey(k.Column)))
                values[k.Column] = _options.DefaultCurrency;
            if (_spec.Keys.All(k => !k.Required || values.ContainsKey(k.Column)))
            {
                result.Key = BuildKey(values);
                result.DisplayKey = result.Key.Replace(KeySeparator, "|", StringComparison.Ordinal);
            }
        }
        catch (IngestValueException)
        {
            // The full parse reports the error.
        }
    }

    public string BuildKey(IReadOnlyDictionary<string, object?> values) =>
        string.Join(KeySeparator, _spec.Keys.Select(k => values.TryGetValue(k.Column, out var v) ? Format(v) : ""));

    /// <summary>Key text of a value; the SQL side builds the same text (see IngestWriter.KeyExpression).</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private void Read(IngestField f, JsonElement value, Dictionary<string, object?> target, List<JsonProperty> extra, JsonProperty source)
    {
        object? parsed = f.Type switch
        {
            IngestFieldType.Ref => IngestValues.Ref(value, f.Name),
            IngestFieldType.Text => IngestValues.Text(value, f.Name, _options.MaxTextLength),
            IngestFieldType.ShortText => IngestValues.Text(value, f.Name, IngestValues.ShortTextLength),
            IngestFieldType.Code => IngestValues.Code(value, f.Name) is { Length: > 0 } c ? c : null,
            IngestFieldType.Tin => IngestValues.Tin(value, f.Name),
            IngestFieldType.Date => IngestValues.Date(value, f.Name),
            IngestFieldType.Timestamp => IngestValues.Timestamp(value, f.Name, _options.SourceUtcOffset),
            IngestFieldType.Money => IngestValues.Money(value, f.Name),
            IngestFieldType.Decimal => IngestValues.Decimal(value, f.Name),
            IngestFieldType.Rate => IngestValues.Rate(value),
            IngestFieldType.Int => IngestValues.Int(value, f.Name),
            IngestFieldType.Bool => IngestValues.Bool(value, f.Name),
            IngestFieldType.Currency => IngestValues.Currency(value, f.Name),
            IngestFieldType.DocKind => IngestValues.DocKind(value, f.Name).ToString(),
            IngestFieldType.Direction => IngestValues.Direction(value, f.Name).ToString(),
            IngestFieldType.Barcodes => IngestValues.Barcodes(value, f.Name),
            IngestFieldType.ItemType => IngestValues.ItemType(value),
            _ => throw new InvalidOperationException($"Unknown field type {f.Type}."),
        };
        // A rate that is not a number (e.g. "Без НДС") is not an error: NULL in the column, the source text in extra.
        if (parsed is null && f.Type is IngestFieldType.Rate or IngestFieldType.ItemType) extra.Add(source);
        if (parsed is not null) target[f.Column] = parsed;
    }

    private List<Dictionary<string, object?>> ReadChildren(JsonElement rows)
    {
        var name = _spec.Children!.Name;
        if (rows.ValueKind != JsonValueKind.Array)
            throw new IngestValueException(IngestCodes.InvalidLines, $"Field '{name}' must be an array.");
        var result = new List<Dictionary<string, object?>>();
        var numbers = new HashSet<int>();
        var position = 0;
        foreach (var row in rows.EnumerateArray())
        {
            position++;
            if (row.ValueKind != JsonValueKind.Object)
                throw new IngestValueException(IngestCodes.InvalidLines, $"Every element of '{name}' must be an object.");
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var extra = new List<JsonProperty>();
            foreach (var p in row.EnumerateObject())
            {
                if (_childFields!.TryGetValue(IngestSchema.NormalizeName(p.Name), out var f) && seen.Add(f.Name))
                {
                    try
                    {
                        if (!IngestValues.IsMissing(p.Value)) Read(f, p.Value, values, extra, p);
                    }
                    catch (IngestValueException ex)
                    {
                        throw new IngestValueException(ex.Code, $"{name}[{position}]: {ex.Message}");
                    }
                    continue;
                }
                extra.Add(p);
            }
            var missing = _spec.Children.Fields.Where(f => f.Required && !values.ContainsKey(f.Column)).Select(f => f.Name).ToList();
            if (missing.Count > 0)
                throw new IngestValueException(IngestCodes.MissingField, $"{name}[{position}]: required field(s) missing: {string.Join(", ", missing)}.");
            var lineNo = values.TryGetValue("line_no", out var n) && n is int number && number > 0 ? number : position;
            if (!numbers.Add(lineNo))
                throw new IngestValueException(IngestCodes.DuplicateLineNo, $"{name}[{position}]: line number {lineNo} is repeated.");
            values["line_no"] = lineNo;
            values["extra"] = WriteObject(extra);
            result.Add(values);
        }
        return result;
    }

    private static string WriteObject(List<JsonProperty> properties)
    {
        if (properties.Count == 0) return "{}";
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in properties)
                if (names.Add(p.Name)) p.WriteTo(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
