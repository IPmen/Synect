using System.Globalization;
using System.Text.RegularExpressions;
using Synect.Application.Analytics;

namespace Synect.Mcp.Responses;

/// <summary>Invalid tool input. The message is shown to the model as is, so it must be clear English without secrets.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// Validates tool arguments and records the normalized request, which every answer echoes back.
/// </summary>
public sealed partial class ToolArgs(McpOptions options)
{
    private readonly Dictionary<string, object?> _request = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Request => _request;

    /// <summary>The period of the answer (echo), or null for tools without one.</summary>
    public object? Period { get; private set; }

    public DateOnly Date(string name, string? value)
    {
        var date = ParseDate(name, value ?? throw new ToolArgumentException($"Parameter '{name}' is required (date in yyyy-MM-dd format, e.g. 2026-10-01)."));
        _request[name] = date;
        return date;
    }

    public DateOnly? OptionalDate(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Date(name, value);
    }

    /// <summary>Inclusive period of local (UTC+5) dates, at most Mcp:MaxPeriodDays long.</summary>
    public DatePeriod DatePeriod(string fromName, string? from, string toName, string? to, bool echo = true)
    {
        var start = Date(fromName, from);
        var end = Date(toName, to);
        if (end < start)
            throw new ToolArgumentException($"'{toName}' ({end:yyyy-MM-dd}) is before '{fromName}' ({start:yyyy-MM-dd}).");
        var period = new DatePeriod(start, end);
        if (period.Days > options.MaxPeriodDays)
            throw new ToolArgumentException(
                $"The period from {start:yyyy-MM-dd} to {end:yyyy-MM-dd} is {period.Days} days; the maximum is {options.MaxPeriodDays} days. Use a shorter period.");
        if (echo) Period = new { From = start, To = end, Days = period.Days };
        return period;
    }

    public void SetPeriodEcho(object period) => Period = period;

    public DatePeriod? OptionalPeriod(string fromName, string? from, string toName, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)) return null;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            throw new ToolArgumentException($"Give both '{fromName}' and '{toName}', or neither.");
        return DatePeriod(fromName, from, toName, to);
    }

    /// <summary>INN: 9 digits (organisation) or 14 digits (individual, PINFL).</summary>
    public string Inn(string name, string? value)
    {
        var inn = value?.Trim();
        if (inn is null || !InnPattern().IsMatch(inn))
            throw new ToolArgumentException($"Parameter '{name}' must be an INN of exactly 9 digits (organisation) or 14 digits (individual), e.g. 301000001.");
        _request[name] = inn;
        return inn;
    }

    public string? OptionalInn(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : Inn(name, value);

    public Guid? OptionalGuid(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Guid.TryParse(value.Trim(), out var id))
            throw new ToolArgumentException($"Parameter '{name}' must be an id (UUID) as returned by another tool, e.g. in 'account_id'.");
        _request[name] = id;
        return id;
    }

    /// <summary>One of the given snake_case values (case-insensitive).</summary>
    public T Enum<T>(string name, string? value, IReadOnlyDictionary<string, T> allowed)
    {
        var key = value?.Trim().ToLowerInvariant();
        if (key is null || !allowed.TryGetValue(key, out var result))
            throw new ToolArgumentException($"Parameter '{name}' must be one of: {string.Join(", ", allowed.Keys)}.");
        _request[name] = key;
        return result;
    }

    public T? OptionalEnum<T>(string name, string? value, IReadOnlyDictionary<string, T> allowed) where T : struct =>
        string.IsNullOrWhiteSpace(value) ? null : Enum(name, value, allowed);

    public int Int(string name, int? value, int fallback, int min, int max)
    {
        var result = value ?? fallback;
        if (result < min || result > max)
            throw new ToolArgumentException($"Parameter '{name}' must be between {min} and {max}.");
        _request[name] = result;
        return result;
    }

    /// <summary>Row limit (default Mcp:DefaultLimit, at most Mcp:MaxLimit) and the cursor from a previous answer.</summary>
    public PageRequest Page(int? limit, string? cursor)
    {
        var rows = Limit(limit);
        if (cursor is not null)
        {
            if (cursor.Length is 0 or > 512 || !CursorPattern().IsMatch(cursor))
                throw new ToolArgumentException("Parameter 'cursor' must be the 'next_cursor' value of a previous answer of the same tool.");
            _request["cursor"] = cursor;
        }
        return new PageRequest(rows, cursor);
    }

    public int Limit(int? limit) => Int("limit", limit, options.DefaultLimit, 1, options.MaxLimit);

    public string? OptionalCurrency(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var currency = value.Trim().ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(currency))
            throw new ToolArgumentException($"Parameter '{name}' must be a currency code as shown in answers: 3 letters (e.g. UZS, USD) or 3 digits (e.g. 840).");
        _request[name] = currency;
        return currency;
    }

    /// <summary>Short code made of digits (Didox document type or status), e.g. "002" or "3".</summary>
    public string? OptionalCode(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.Trim();
        if (!CodePattern().IsMatch(code))
            throw new ToolArgumentException($"Parameter '{name}' must be a numeric Didox code of 1-4 digits, e.g. 002 or 3.");
        _request[name] = code;
        return code;
    }

    /// <summary>Free text used as a filter (document or contract number): at most 100 visible characters.</summary>
    public string? OptionalText(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 100 || text.Any(char.IsControl))
            throw new ToolArgumentException($"Parameter '{name}' must be at most 100 characters without control characters.");
        _request[name] = text;
        return text;
    }

    private static DateOnly ParseDate(string name, string value)
    {
        var text = value.Trim();
        if (!DatePattern().IsMatch(text)
            || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ToolArgumentException($"Parameter '{name}' must be a date in yyyy-MM-dd format, e.g. 2026-10-01 (got '{Echo(text)}').");
        if (date.Year is < 2000 or > 2100)
            throw new ToolArgumentException($"Parameter '{name}' must be between 2000-01-01 and 2100-12-31.");
        return date;
    }

    private static string Echo(string text) => text.Length > 20 ? text[..20] + "…" : new string(text.Where(c => !char.IsControl(c)).ToArray());

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(\d{9}|\d{14})$")]
    private static partial Regex InnPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex CursorPattern();

    [GeneratedRegex(@"^([A-Z]{3}|[0-9]{3})$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex(@"^\d{1,4}$")]
    private static partial Regex CodePattern();
}

/// <summary>Accepted values of enumerations in tool arguments (snake_case).</summary>
public static class ToolEnums
{
    public static readonly IReadOnlyDictionary<string, FlowDirection> Direction = new Dictionary<string, FlowDirection>
    {
        ["incoming"] = FlowDirection.Incoming,
        ["outgoing"] = FlowDirection.Outgoing,
    };

    public static readonly IReadOnlyDictionary<string, Granularity> Granularity = new Dictionary<string, Granularity>
    {
        ["day"] = Application.Analytics.Granularity.Day,
        ["week"] = Application.Analytics.Granularity.Week,
        ["month"] = Application.Analytics.Granularity.Month,
    };

    public static readonly IReadOnlyDictionary<string, DateBasis> DateBasis = new Dictionary<string, DateBasis>
    {
        ["document_date"] = Application.Analytics.DateBasis.DocumentDate,
        ["source_created"] = Application.Analytics.DateBasis.SourceCreated,
        ["source_updated"] = Application.Analytics.DateBasis.SourceUpdated,
    };

    public static readonly IReadOnlyDictionary<string, Domain.Edo.EdoDocumentOwner> Owner = new Dictionary<string, Domain.Edo.EdoDocumentOwner>
    {
        ["incoming"] = Domain.Edo.EdoDocumentOwner.Incoming,
        ["outgoing"] = Domain.Edo.EdoDocumentOwner.Outgoing,
    };

    public static readonly IReadOnlyDictionary<string, Application.Analytics.Counterparties.CounterpartyChangeKind> ChangeKind =
        new Dictionary<string, Application.Analytics.Counterparties.CounterpartyChangeKind>
        {
            ["new"] = Application.Analytics.Counterparties.CounterpartyChangeKind.New,
            ["lost"] = Application.Analytics.Counterparties.CounterpartyChangeKind.Lost,
        };

    public static readonly IReadOnlyDictionary<string, string> Source = new Dictionary<string, string>
    {
        ["all"] = "all",
        ["bank"] = "bank",
        ["edo"] = "edo",
    };
}
