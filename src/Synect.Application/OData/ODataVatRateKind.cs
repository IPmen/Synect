using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Synect.Application.OData;

/// <summary>
/// Whether the VAT rate attribute of document lines is an enumeration (value names such as НДС12, read by the profile's
/// vat_rate transform) or a reference to a catalog (СтавкаНДС_Key, a GUID; the starting profile cannot read it). Used by
/// probe-odata. Evidence from a real line wins over $metadata.
/// </summary>
public static class ODataVatRateKind
{
    public const string Enumeration = "enumeration";
    public const string Reference = "reference";
    public const string NotFound = "not found";

    public sealed record Result(string Kind, string Evidence);

    public static Result Detect(string? metadataXml, string resource, string table, string property, JsonElement? line)
    {
        if (line is { ValueKind: JsonValueKind.Object } row)
        {
            if (row.TryGetProperty(property + "_Key", out var key) && key.ValueKind == JsonValueKind.String && Guid.TryParse(key.GetString(), out _))
                return new Result(Reference, $"first line has {property}_Key (GUID)");
            if (row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return Guid.TryParse(value.GetString(), out _)
                    ? new Result(Reference, $"first line has {property} as a GUID")
                    : new Result(Enumeration, $"first line has {property} as text");
        }

        if (metadataXml is null) return new Result(NotFound, "no $metadata and no line with the attribute");
        XDocument doc;
        try
        {
            doc = XDocument.Parse(metadataXml);
        }
        catch (XmlException)
        {
            return new Result(NotFound, "$metadata is not valid XML");
        }
        // TODO(verify-real): the row type of a table part is assumed to be named "{resource}_{table}_RowType" (or to start with
        // "{resource}_{table}"), with the attribute as Property "{name}" (Edm.String for an enumeration) or "{name}_Key" (Edm.Guid).
        var rowType = doc.Descendants().Where(e => e.Name.LocalName == "EntityType" || e.Name.LocalName == "ComplexType")
            .FirstOrDefault(e => (string?)e.Attribute("Name") is { } n && (n == $"{resource}_{table}_RowType" || n.StartsWith($"{resource}_{table}", StringComparison.Ordinal)));
        if (rowType is null) return new Result(NotFound, $"no row type of {resource}.{table} in $metadata");
        var properties = rowType.Elements().Where(e => e.Name.LocalName == "Property")
            .ToDictionary(e => (string?)e.Attribute("Name") ?? "", e => (string?)e.Attribute("Type") ?? "", StringComparer.Ordinal);
        if (properties.TryGetValue(property + "_Key", out var keyType))
            return new Result(Reference, $"$metadata: {property}_Key {keyType}");
        if (properties.TryGetValue(property, out var type))
            return type == "Edm.Guid" ? new Result(Reference, $"$metadata: {property} Edm.Guid") : new Result(Enumeration, $"$metadata: {property} {type}");
        return new Result(NotFound, $"$metadata: {resource}.{table} has no {property}");
    }
}
