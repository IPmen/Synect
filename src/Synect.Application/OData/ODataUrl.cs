using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Synect.Application.OData;

/// <summary>
/// Literals of OData v3 filters as 1C writes them: strings in single quotes (a quote doubled), guid'…', datetime'…'
/// (local time of the application, no zone: 1C stores dates without a time zone).
/// </summary>
public static class ODataLiteral
{
    public static string String(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Guid(Guid value) => "guid'" + value.ToString("D") + "'";

    /// <summary>guid'…' of a ref given as text; refuses anything that is not a GUID (nothing else reaches the URL).</summary>
    public static string Guid(string value) =>
        System.Guid.TryParse(value, out var g) ? Guid(g) : throw new ArgumentException("Not a GUID reference.", nameof(value));

    public static string DateTime(DateTime value) => "datetime'" + value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "'";

    public static string DateTime(DateOnly value) => DateTime(value.ToDateTime(TimeOnly.MinValue));
}

/// <summary>
/// Builds request URLs relative to the service root: resource names (Cyrillic, e.g. Catalog_Контрагенты) and query values
/// are percent-encoded as UTF-8; $format=json is always added. Resource and property names are checked against a strict
/// pattern (letters, digits, '_'; '/' between path parts in $select and $orderby), so nothing else can be injected.
/// </summary>
public static partial class ODataUrl
{
    [GeneratedRegex(@"^[\p{L}\p{N}_]{1,128}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[\p{L}\p{N}_]{1,128}(/[\p{L}\p{N}_]{1,128})*$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[\p{L}\p{N}_]{1,128}(/[\p{L}\p{N}_]{1,128})*( (asc|desc))?$")]
    private static partial Regex OrderPattern();

    public static bool IsName(string name) => NamePattern().IsMatch(name);

    public static string CheckName(string name) =>
        IsName(name) ? name : throw new ArgumentException("Not a valid OData resource or property name.", nameof(name));

    /// <summary>
    /// Percent-encoding of a path segment or query value (RFC 3986 unreserved characters stay as they are).
    /// TODO(verify-real): 1C is assumed to decode %27 (') and percent-encoded UTF-8 Cyrillic in the path and in $filter.
    /// </summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);

    public static string Build(ODataQuery q)
    {
        var path = new StringBuilder(Encode(CheckName(q.Resource)));
        if (q.Key is { } key) path.Append('(').Append(Encode(key)).Append(')');
        var parts = new List<string> { "$format=json" };
        if (q.Filter is { Length: > 0 } filter) parts.Add("$filter=" + Encode(filter));
        if (q.Select is { Count: > 0 } select)
        {
            foreach (var s in select)
                if (!PathPattern().IsMatch(s)) throw new ArgumentException("Not a valid $select item.", nameof(q));
            parts.Add("$select=" + Encode(string.Join(",", select)));
        }
        if (q.OrderBy is { Length: > 0 } order)
        {
            foreach (var o in order.Split(','))
                if (!OrderPattern().IsMatch(o.Trim())) throw new ArgumentException("Not a valid $orderby item.", nameof(q));
            parts.Add("$orderby=" + Encode(order));
        }
        if (q.Top is { } top) parts.Add("$top=" + top.ToString(CultureInfo.InvariantCulture));
        if (q.Skip is { } skip) parts.Add("$skip=" + skip.ToString(CultureInfo.InvariantCulture));
        if (q.InlineCount) parts.Add("$inlinecount=allpages");
        return path + "?" + string.Join("&", parts);
    }

    public static string Count(string resource, string? filter) =>
        Encode(CheckName(resource)) + "/$count" + (filter is { Length: > 0 } ? "?$filter=" + Encode(filter) : "");

    public const string Metadata = "$metadata";

    /// <summary>The service document (list of resources).</summary>
    public const string ServiceDocument = "?$format=json";
}
