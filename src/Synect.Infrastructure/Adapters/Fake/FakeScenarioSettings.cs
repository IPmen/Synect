using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synect.Application.Adapters;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// The demo settings of a fake connection, kept in the connection's Settings under <see cref="Key"/>, so that every
/// process (DevTools seeding, the Worker's scheduled loads) generates the same data for it. Without them the Worker
/// would load a demo connection with the default scenario and mix foreign accounts and documents into the demo.
/// </summary>
public static class FakeScenarioSettings
{
    public const string Key = "fake_scenario";

    public static JsonObject Of(FakeBankScenario s) => new()
    {
        ["seed"] = s.Seed,
        ["account_count"] = s.AccountCount,
        ["account_currencies"] = new JsonArray(s.AccountCurrencies.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["counterparty_pool_size"] = s.CounterpartyPoolSize,
        ["no_inn_share"] = s.NoInnShare,
        ["min_operations_per_day"] = s.MinOperationsPerDay,
        ["max_operations_per_day"] = s.MaxOperationsPerDay,
    };

    public static JsonObject Of(FakeEdoScenario s) => new()
    {
        ["seed"] = s.Seed,
        ["document_count"] = s.DocumentCount,
        ["spread_days"] = s.SpreadDays,
        ["counterparty_pool_size"] = s.CounterpartyPoolSize,
        ["page_size"] = s.PageSize,
        ["base_time"] = s.BaseTime.ToString("O", CultureInfo.InvariantCulture),
    };

    public static void Apply(FakeBankScenario s, JsonElement e)
    {
        if (e.TryGetProperty("seed", out var v)) s.Seed = v.GetInt32();
        if (e.TryGetProperty("account_count", out v)) s.AccountCount = v.GetInt32();
        if (e.TryGetProperty("account_currencies", out v)) s.AccountCurrencies = v.EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (e.TryGetProperty("counterparty_pool_size", out v)) s.CounterpartyPoolSize = v.GetInt32();
        if (e.TryGetProperty("no_inn_share", out v)) s.NoInnShare = v.GetDouble();
        if (e.TryGetProperty("min_operations_per_day", out v)) s.MinOperationsPerDay = v.GetInt32();
        if (e.TryGetProperty("max_operations_per_day", out v)) s.MaxOperationsPerDay = v.GetInt32();
    }

    public static void Apply(FakeEdoScenario s, JsonElement e)
    {
        if (e.TryGetProperty("seed", out var v)) s.Seed = v.GetInt32();
        if (e.TryGetProperty("document_count", out v)) s.DocumentCount = v.GetInt32();
        if (e.TryGetProperty("spread_days", out v)) s.SpreadDays = v.ValueKind == JsonValueKind.Null ? null : v.GetInt32();
        if (e.TryGetProperty("counterparty_pool_size", out v)) s.CounterpartyPoolSize = v.GetInt32();
        if (e.TryGetProperty("page_size", out v)) s.PageSize = v.GetInt32();
        if (e.TryGetProperty("base_time", out v)) s.BaseTime = DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture);
    }

    /// <summary>The settings stored with the connection of the adapter call, or null when it has none.</summary>
    public static JsonElement? Read(AdapterContext context) =>
        context.Settings.TryGetValue(Key, out var json) && !string.IsNullOrWhiteSpace(json)
            ? JsonDocument.Parse(json).RootElement.Clone()
            : null;

    /// <summary>The scenario stored in a connection Settings JSON, or null.</summary>
    public static JsonElement? Stored(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return null;
        using var doc = JsonDocument.Parse(settings);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(Key, out var e) ? e.Clone() : null;
    }

    /// <summary>The connection Settings JSON with <paramref name="scenario"/> stored under <see cref="Key"/>.</summary>
    public static string Store(string? settings, JsonObject scenario)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(settings) ? "{}" : settings) as JsonObject ?? new JsonObject();
        root[Key] = scenario;
        return root.ToJsonString();
    }
}
