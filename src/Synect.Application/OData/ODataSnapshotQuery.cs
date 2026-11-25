namespace Synect.Application.OData;

/// <summary>
/// How a snapshot source of a profile becomes a request: accounts found by code in their chart, parameters of the virtual
/// table with their placeholders filled. Shared by the loader and DevTools probe-odata, so that the probe asks exactly what
/// the load will ask.
/// </summary>
public static class ODataSnapshotQuery
{
    /// <summary>
    /// "Account_Key eq guid'…' or …" for the profile's codes found in the chart (key, Code), or null when none is found;
    /// <paramref name="missing"/> gets the codes that matched nothing.
    /// </summary>
    public static string? AccountCondition(ODataAccounts accounts, IEnumerable<(string Key, string? Code)> chart, out List<string> missing)
    {
        var rows = chart.Where(x => !string.IsNullOrWhiteSpace(x.Code)).Select(x => (x.Key, Code: x.Code!.Trim())).ToList();
        var keys = new List<string>();
        missing = [];
        foreach (var code in accounts.Codes)
        {
            var found = code.EndsWith('*')
                ? rows.Where(x => x.Code.StartsWith(code[..^1], StringComparison.Ordinal)).Select(x => x.Key).ToList()
                : rows.Where(x => x.Code == code).Select(x => x.Key).ToList();
            if (found.Count == 0) missing.Add(code);
            keys.AddRange(found);
        }
        keys = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return keys.Count == 0 ? null : string.Join(" or ", keys.Select(k => $"{ODataUrl.CheckName(accounts.Property)} eq {ODataLiteral.Guid(k)}"));
    }

    /// <summary>
    /// The request of a snapshot source (without paging). A condition parameter that names {organization} is left out when
    /// the connection has no organization; datetime parameters are the local <paramref name="today"/> at 00:00:00 or 23:59:59.
    /// </summary>
    public static ODataQuery Build(ODataSource source, string? organization, string? accounts, DateOnly today, string? filter)
    {
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var (name, p) in source.Parameters ?? [])
        {
            if (p.Type == "datetime")
            {
                var at = p.Value == "{today_start}" ? today.ToDateTime(TimeOnly.MinValue) : today.ToDateTime(new TimeOnly(23, 59, 59));
                parameters.Add(new(name, ODataLiteral.DateTime(at)));
                continue;
            }
            if (organization is null && p.Value.Contains("{organization}", StringComparison.Ordinal)) continue;
            var text = p.Value
                .Replace("{organization}", organization is null ? "" : ODataLiteral.Guid(organization), StringComparison.Ordinal)
                .Replace("{accounts}", accounts ?? "", StringComparison.Ordinal);
            parameters.Add(new(name, ODataLiteral.String(text)));
        }
        return new ODataQuery(source.Resource.Name)
        {
            VirtualTable = source.VirtualTable?.Name, Parameters = parameters, Filter = filter, OrderBy = source.OrderBy,
        };
    }
}
