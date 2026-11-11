using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Freshness;

namespace Synect.Application.Analytics;

/// <summary>Inclusive range of local (UTC+5) dates.</summary>
public sealed record DatePeriod
{
    public DatePeriod(DateOnly from, DateOnly to)
    {
        if (to < from) throw new ArgumentException("Period end is before its start.", nameof(to));
        From = from;
        To = to;
    }

    public DateOnly From { get; }
    public DateOnly To { get; }
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>First days of the calendar months the period touches.</summary>
    public (DateOnly FirstMonth, DateOnly LastMonth) Months =>
        (new DateOnly(From.Year, From.Month, 1), new DateOnly(To.Year, To.Month, 1));

    /// <summary>The period of the same length right before this one.</summary>
    public DatePeriod Previous() => new(From.AddDays(-Days), From.AddDays(-1));
}

/// <summary>Which date of a Didox document selects it into a period.</summary>
public enum DateBasis
{
    DocumentDate,
    SourceCreated,
    SourceUpdated,
}

public enum Granularity
{
    Day,
    Week,
    Month,
}

public enum FlowDirection
{
    Incoming,
    Outgoing,
}

/// <summary>Amount in minor units of one currency. Amounts of different currencies are never added.</summary>
public sealed record MoneyAmount(string Currency, long Amount);

/// <summary>Limit defaults to Analytics:DefaultPageSize (100) and is capped at MaxPageSize (1000).</summary>
public sealed record PageRequest(int? Limit = null, string? Cursor = null);

/// <summary>A page of a list with a stable order. NextCursor is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Freshness of every source of the tenant at the time the answer was produced.</summary>
public sealed record DataFreshness(DateTimeOffset GeneratedAt, IReadOnlyList<SourceFreshness> Sources);

/// <summary>Every analytics answer carries the data and its freshness.</summary>
public sealed record AnalyticsResult<T>(T Data, DataFreshness Freshness);

public sealed class AnalyticsTimeoutException(TimeSpan timeout, Exception? inner = null)
    : Exception($"The analytics query did not finish within {timeout}.", inner);

/// <summary>Opaque keyset cursor: base64url of a JSON array of the sort key values of the last row.</summary>
public static class Cursor
{
    public static string Encode(params object?[] values) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(values.Select(v => v switch
        {
            null => null,
            DateOnly d => d.ToString("O"),
            DateTimeOffset t => t.UtcDateTime.ToString("O"),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        }).ToArray()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string?[] Decode(string cursor, int expectedParts)
    {
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = JsonSerializer.Deserialize<string?[]>(Convert.FromBase64String(b64))!;
            if (parts.Length != expectedParts) throw new FormatException();
            return parts;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor), ex);
        }
    }
}

/// <summary>
/// Builds parameterized SQL. Text appended with <see cref="Raw"/> must be constant SQL written in code;
/// every value goes through <see cref="P"/> and becomes a bind parameter.
/// </summary>
internal sealed class Sql
{
    private readonly StringBuilder _text = new();
    private readonly List<object> _args = [];

    public Sql Raw(string text)
    {
        _text.Append(text.Replace("{", "{{").Replace("}", "}}"));
        return this;
    }

    public Sql P(object value)
    {
        _text.Append('{').Append(_args.Count).Append('}');
        _args.Add(value);
        return this;
    }

    public FormattableString Build() => FormattableStringFactory.Create(_text.ToString(), _args.ToArray());
}

/// <summary>
/// Shared plumbing of the analytics services: tenant from the current context (never from arguments),
/// time zone, paging, timeout and the DataFreshness block. Reads only the Synect database.
/// </summary>
public sealed class AnalyticsContext(
    ISynectDbContext db,
    ITenantContext tenant,
    ISourceFreshnessQuery freshness,
    IOptions<AnalyticsOptions> options,
    TimeProvider time)
{
    public ISynectDbContext Db => db;
    public AnalyticsOptions Options => options.Value;

    public Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Analytics requires a tenant context.");

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Options.UtcOffset).DateTime);

    /// <summary>UTC instants of the local period: [From 00:00 local, To+1 00:00 local).</summary>
    public (DateTimeOffset From, DateTimeOffset ToExclusive) UtcRange(DatePeriod period) =>
        (LocalMidnightUtc(period.From), LocalMidnightUtc(period.To.AddDays(1)));

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Options.UtcOffset).DateTime);

    public int Limit(PageRequest? page) =>
        Math.Clamp(page?.Limit ?? Options.DefaultPageSize, 1, Options.MaxPageSize);

    /// <summary>Runs the query in a tenant transaction with the timeout and attaches DataFreshness.</summary>
    public async Task<AnalyticsResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken ct)
    {
        var tenantId = TenantId;
        var timeout = Options.QueryTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await db.InTransactionAsync(async token =>
            {
                if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    // Equivalent of SET LOCAL statement_timeout, but with a bind parameter.
                    var ms = ((int)Math.Max(1, timeout.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {ms}, true)", token);
                    if (Options.MaxParallelWorkersPerGather is { } workers)
                    {
                        var value = workers.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await db.Database.ExecuteSqlAsync($"SELECT set_config('max_parallel_workers_per_gather', {value}, true)", token);
                    }
                }
                var data = await query(token);
                var sources = await freshness.GetAsync(tenantId, token);
                return new AnalyticsResult<T>(data, new DataFreshness(time.GetUtcNow(), sources));
            }, cts.Token);
        }
        // Our own timeout fired (and the caller did not cancel): whatever failed - opening the connection (Npgsql may then
        // throw a SocketException from an interrupted DNS lookup instead of OperationCanceledException), the query or the
        // freshness read - it is a timeout. statement_timeout on the server (57014) is one too.
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && (cts.IsCancellationRequested || ex is OperationCanceledException || ex is DbException { SqlState: "57014" }
                                       || ex.InnerException is DbException { SqlState: "57014" }))
        {
            throw new AnalyticsTimeoutException(timeout, ex);
        }
    }

    internal Task<List<TRow>> QueryAsync<TRow>(Sql sql, CancellationToken ct) =>
        db.Database.SqlQuery<TRow>(sql.Build()).ToListAsync(ct);

    /// <summary>Takes limit+1 rows: the extra row only tells whether there is a next page.</summary>
    internal static Page<TOut> ToPage<TRow, TOut>(List<TRow> rows, int limit, Func<TRow, TOut> map, Func<TRow, string> cursorOf)
    {
        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).ToList();
        return new Page<TOut>(items.Select(map).ToList(), hasMore ? cursorOf(items[^1]) : null);
    }

    private DateTimeOffset LocalMidnightUtc(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Options.UtcOffset).ToUniversalTime();
}
