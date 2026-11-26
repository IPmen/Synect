using Synect.Application.Analytics;

namespace Synect.Mcp.Responses;

/// <summary>
/// Totals of a whole paged list for the 'summary' block of the first page: the number of rows and the sums per currency
/// (optionally per group, e.g. the reason), so that the model states the count and the amount first without paging
/// through the list and adding amounts itself. Read only for the first page (no cursor); a list longer than
/// <see cref="MaxRows"/> is summarized up to that limit with complete = false.
/// </summary>
public static class ListSummary
{
    public const int MaxRows = 2000;

    /// <summary>One row's contribution: currency, amount, optional remaining amount (documents) and group.</summary>
    public readonly record struct Item(string Currency, long Amount, long? Remaining = null, string? Group = null);

    /// <summary>Runs the first page and, when the list goes on, the remaining pages (MaxLimit rows each) for the totals.</summary>
    public static async Task<(AnalyticsResult<Page<T>> First, object? Summary)> FirstPageAsync<T>(
        Func<PageRequest, CancellationToken, Task<AnalyticsResult<Page<T>>>> fetch, PageRequest page, int maxLimit,
        Func<T, Item> item, CancellationToken ct)
    {
        var first = await fetch(page, ct);
        if (page.Cursor is not null) return (first, null);

        var rows = first.Data.Items.Select(item).ToList();
        var cursor = first.Data.NextCursor;
        while (cursor is not null && rows.Count < MaxRows)
        {
            var next = await fetch(new PageRequest(maxLimit, cursor), ct);
            rows.AddRange(next.Data.Items.Select(item));
            cursor = next.Data.NextCursor;
        }
        return (first, Build(rows.Take(MaxRows).ToList(), complete: cursor is null && rows.Count <= MaxRows));
    }

    public static object Build(IReadOnlyList<Item> rows, bool complete = true)
    {
        var groups = rows.Where(r => r.Group is not null).ToList();
        return new
        {
            Rows = rows.Count,
            Complete = complete,
            TotalsByCurrency = Totals(rows),
            ByGroup = groups.Count == 0
                ? null
                : groups.GroupBy(r => r.Group!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .Select(g => new { Group = g.Key, Rows = g.Count(), TotalsByCurrency = Totals(g.ToList()) })
                    .ToList(),
        };
    }

    private static List<object> Totals(IReadOnlyList<Item> rows) => rows
        .GroupBy(r => r.Currency, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (object)new
        {
            Currency = g.Key,
            Rows = g.Count(),
            Amount = MoneyValue.Of(g.Sum(r => r.Amount), g.Key),
            Remaining = g.Any(r => r.Remaining is not null) ? MoneyValue.Of(g.Sum(r => r.Remaining ?? 0), g.Key) : null,
        })
        .ToList();
}
