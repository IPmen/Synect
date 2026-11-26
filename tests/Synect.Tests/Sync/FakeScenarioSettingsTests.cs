using Synect.Application.Adapters;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.Tests.Sync;

/// <summary>
/// A demo connection carries its fake scenario in its settings, so that the Worker (another process) generates the same
/// data as the seed instead of the default fake data.
/// </summary>
public class FakeScenarioSettingsTests
{
    private static AdapterContext Context(Guid connection, string? settings)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FakeScenarioSettings.Stored(settings) is { } e) dict[FakeScenarioSettings.Key] = e.GetRawText();
        return new AdapterContext(Guid.NewGuid(), connection, FakeBankAdapter.Code, dict, null, null!);
    }

    [Fact]
    public void A_bank_scenario_stored_by_the_seed_is_applied_by_another_process()
    {
        var seed = new FakeBankScenario
        {
            Seed = 101, AccountCount = 2, AccountCurrencies = ["000", "840"], CounterpartyPoolSize = 40, NoInnShare = 0.05, MaxOperationsPerDay = 8,
        };
        var settings = FakeScenarioSettings.Store("""{"other":"kept"}""", FakeScenarioSettings.Of(seed));
        Assert.Contains("\"other\":\"kept\"", settings);

        var worker = new FakeBankDataSource();
        var connection = Guid.NewGuid();
        worker.Configure(Context(connection, settings));
        var s = worker.For(connection);
        Assert.Equal(101, s.Seed);
        Assert.Equal(2, s.AccountCount);
        Assert.Equal(["000", "840"], s.AccountCurrencies);
        Assert.Equal(40, s.CounterpartyPoolSize);
        Assert.Equal(0.05, s.NoInnShare);
        Assert.Equal(8, s.MaxOperationsPerDay);
    }

    [Fact]
    public void An_edo_scenario_keeps_its_base_time_and_a_connection_without_settings_keeps_the_defaults()
    {
        var baseTime = new DateTimeOffset(2025, 11, 26, 8, 32, 0, TimeSpan.Zero);
        var seed = new FakeEdoScenario { Seed = 201, DocumentCount = 400, SpreadDays = 365, CounterpartyPoolSize = 40, PageSize = 100, BaseTime = baseTime };
        var settings = FakeScenarioSettings.Store(null, FakeScenarioSettings.Of(seed));

        var worker = new FakeEdoDataSource(TimeProvider.System);
        var connection = Guid.NewGuid();
        worker.Configure(Context(connection, settings));
        var s = worker.For(connection);
        Assert.Equal((201, 400, 365, 40, 100, baseTime), (s.Seed, s.DocumentCount, s.SpreadDays, s.CounterpartyPoolSize, s.PageSize, s.BaseTime));

        var plain = Guid.NewGuid();
        worker.Configure(Context(plain, "{}"));
        Assert.Equal(new FakeEdoScenario().Seed, worker.For(plain).Seed);
        Assert.Null(FakeScenarioSettings.Stored("{}"));
    }
}
