using System.Globalization;
using Synect.Application.Analytics;

namespace Synect.DevTools;

/// <summary>Minimal "--key value" parser.</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            result._values[key] = value;
        }
        return result;
    }

    public CliArgs With(string key, string value)
    {
        var copy = new CliArgs();
        foreach (var (k, v) in _values) copy._values[k] = v;
        copy._values[key] = value;
        return copy;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public int Int(string key, int fallback) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public DateOnly Date(string key, DateOnly fallback) => Get(key) is { } v ? DateOnly.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public T Enum<T>(string key, T fallback) where T : struct, System.Enum =>
        Get(key) is { } v ? System.Enum.Parse<T>(v, ignoreCase: true) : fallback;

    /// <summary>--from/--to, defaulting to the last <paramref name="defaultDays"/> days up to <paramref name="today"/>.</summary>
    public DatePeriod Period(DateOnly today, int defaultDays, string fromKey = "from", string toKey = "to") =>
        new(Date(fromKey, today.AddDays(-defaultDays + 1)), Date(toKey, today));

    public PageRequest Page() => new(Get("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null, Get("cursor"));
}
