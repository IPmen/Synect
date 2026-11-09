using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Counterparties;
using Synect.Application.Sync;
using Synect.Domain.Banking;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Counterparties;

/// <summary>
/// Deterministic data for name search (PostgreSQL). Test clock: 2026-11-07. Tenant S has several spellings per INN,
/// payments without INN and 60 similar names for paging; tenant T has the same INN and name with other amounts.
/// Aliases are filled by the rebuild (as DevTools rebuild-counterparty-aliases does).
/// </summary>
public sealed class CounterpartySearchFixture : IAsyncLifetime
{
    public const string Romashka = "311111111";
    public const string Rassvet = "322222222";
    public const string Romashkino = "333333333";
    public const string Railways = "344444444";
    public const string Pinfl = "31234567890123";

    private readonly PostgresFixture _pg = new();
    public bool Available => _pg.Available;
    public TestHost Host => _pg.Host;
    public Guid TenantS { get; private set; }
    public Guid TenantT { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;
        TenantS = await Host.CreateTenantAsync("Search S");
        TenantT = await Host.CreateTenantAsync("Search T");

        await SeedAsync(TenantS, (bank, edo, uzs, usd, add, doc) =>
        {
            add(uzs, Romashka, "ООО «Ромашка»", 100_00, new(2026, 10, 1));
            add(uzs, Romashka, "ООО «Ромашка»", 200_00, new(2026, 10, 2));
            add(uzs, Romashka, "ООО «Ромашка»", 300_00, new(2026, 10, 3));
            add(uzs, Romashka, "ROMASHKA MCHJ", 50_00, new(2026, 9, 1));
            add(usd, Romashka, "ROMASHKA MCHJ", 7_00, new(2026, 9, 2));
            doc(Romashka, "Romashka MChJ", 1_000_00, new(2026, 10, 5));
            add(uzs, Rassvet, "Рассвет Савдо", 10_00, new(2026, 8, 1));
            add(uzs, Rassvet, "Рассвет Савдо", 10_00, new(2026, 8, 2));
            add(uzs, Romashkino, "Ромашкино Агро", 5_00, new(2026, 7, 1));
            doc(Railways, "\"Ўзбекистон темир йўллари\" АЖ", 9_000_00, new(2026, 6, 1));
            add(uzs, Pinfl, "YaTT Karimov Jasur", 1_00, new(2026, 5, 1));
            add(uzs, null, "Ромашка", 1_00, new(2026, 10, 10));   // without INN: never found by name
            add(uzs, null, "Ромашка", 2_00, new(2026, 10, 11));
            for (var i = 0; i < 60; i++)
                add(uzs, (350000000 + i).ToString(), $"Тест Компания {i:00}", (i + 1) * 100, new(2026, 10, 1 + i % 28));
        });
        await SeedAsync(TenantT, (bank, edo, uzs, usd, add, doc) =>
        {
            add(uzs, Romashka, "ООО «Ромашка»", 777_00, new(2026, 10, 1));
        });
        await RebuildAsync(TenantS);
        await RebuildAsync(TenantT);
    }

    public ValueTask DisposeAsync() => _pg.DisposeAsync();

    public Task<int> RebuildAsync(Guid tenant) =>
        Host.InTenantAsync(tenant, sp => sp.GetRequiredService<CounterpartyAliasRebuilder>().RebuildAsync(CancellationToken.None));

    public Task<AnalyticsResult<CounterpartySearchResult>> FindAsync(Guid tenant, string query, int? limit = null, string? cursor = null) =>
        Host.InTenantAsync(tenant, sp => sp.GetRequiredService<CounterpartySearch>().FindAsync(query, new PageRequest(limit, cursor)));

    private delegate void AddTx(BankAccount account, string? inn, string name, long amount, DateOnly date);
    private delegate void AddDoc(string inn, string name, long total, DateOnly date);

    private async Task SeedAsync(Guid tenant, Action<Guid, Guid, BankAccount, BankAccount, AddTx, AddDoc> fill)
    {
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var edo = await Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        var now = Host.Time.Now;
        await Host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                BankAccount Account(string number, string currency) => new()
                {
                    ConnectionId = bank, AccountNumber = number, Mfo = "00974", Currency = currency, Name = currency, CreatedAt = now, UpdatedAt = now,
                };
                var uzs = Account("20208000100000000001", "UZS");
                var usd = Account("20208840100000000002", "USD");
                db.BankAccounts.AddRange(uzs, usd);
                var n = 0;
                fill(bank, edo, uzs, usd,
                    (account, inn, name, amount, date) => db.BankTransactions.Add(new BankTransaction
                    {
                        ConnectionId = bank, BankAccountId = account.Id, OperationId = "S" + n++, Amount = amount, Currency = account.Currency,
                        Direction = TransactionDirection.Outgoing, CounterpartyInn = inn, CounterpartyName = name, Purpose = "p",
                        PostingDate = date, ValueDate = date, IsFinal = true, SourceState = "3", SourceHash = new string('0', 64),
                        CreatedAt = now, UpdatedAt = now,
                    }),
                    (inn, name, total, date) =>
                    {
                        var d = new EdoDocument
                        {
                            ConnectionId = edo, ExternalId = "D" + n++, DocType = "002", Owner = EdoDocumentOwner.Incoming, Number = "N" + n,
                            DocumentDate = date, CounterpartyInn = inn, CounterpartyName = name, TotalSum = total, TotalVatSum = 0,
                            TotalDeliverySum = total, TotalDeliverySumWithVat = total, SourceHash = new string('0', 64), CreatedAt = now, UpdatedAt = now,
                        };
                        d.ApplyStatus("3", now, null);
                        db.EdoDocuments.Add(d);
                    });
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return true;
        });
    }
}

public class CounterpartySearchTests(CounterpartySearchFixture f) : IClassFixture<CounterpartySearchFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private async Task<CounterpartySearchResult> Find(string query, Guid? tenant = null, int? limit = null, string? cursor = null)
    {
        var r = await f.FindAsync(tenant ?? f.TenantS, query, limit, cursor);
        Assert.Equal(2, r.Freshness.Sources.Count);
        return r.Data;
    }

    [Theory]
    [InlineData("Ромашка")]
    [InlineData("ромашка")]
    [InlineData("ООО Ромашка")]
    [InlineData("Romashka")]
    [InlineData("Romashka MChJ")]
    [InlineData("«РОМАШКА» МЧЖ")]
    [InlineData("Рамашка")]   // typo
    [InlineData("Romaska")]   // typo
    [InlineData("Ромашк")]    // fragment
    public async Task Finds_a_counterparty_by_any_spelling_or_typo(string query)
    {
        RequirePostgres();
        var r = await Find(query);
        Assert.Equal(CounterpartySearchFixture.Romashka, r.Candidates.Items[0].Inn);
    }

    [Fact]
    public async Task Keeps_every_spelling_and_takes_the_most_frequent_as_the_main_name()
    {
        RequirePostgres();
        var c = (await Find("Ромашка")).Candidates.Items[0];
        Assert.Equal("ООО «Ромашка»", c.Name);                  // 3 operations
        Assert.Equal(["ROMASHKA MCHJ", "Romashka MChJ"], c.OtherNames.Order(StringComparer.Ordinal));
        Assert.Equal(new DateOnly(2026, 10, 3), c.LastPaymentDate);
        Assert.Equal(new DateOnly(2026, 10, 5), c.LastDocumentDate);

        // Bank turnover per currency, never added together; Didox totals separately.
        Assert.Equal(650_00, c.BankTurnover.Single(t => t.Currency == "UZS").Outgoing);
        Assert.Equal(7_00, c.BankTurnover.Single(t => t.Currency == "USD").Outgoing);
        Assert.Equal(1_000_00, Assert.Single(c.DocumentTurnover).IncomingSum);
        Assert.Equal(6, c.Activity);

        var aliases = await f.Host.InTenantAsync(f.TenantS, sp => sp.GetRequiredService<ISynectDbContext>()
            .InTransactionAsync(ct => sp.GetRequiredService<ISynectDbContext>().CounterpartyAliases
                .Where(a => a.CounterpartyInn == CounterpartySearchFixture.Romashka).ToListAsync(ct), CancellationToken.None));
        Assert.Equal(3, aliases.Count);
        Assert.All(aliases, a => Assert.Equal("romashka", a.NormalizedName));
        Assert.Equal(3, aliases.Single(a => a.Name == "ООО «Ромашка»").Occurrences);
        Assert.Equal(2, aliases.Single(a => a.Name == "ROMASHKA MCHJ").Occurrences);
        Assert.Equal(CounterpartyNameSource.Edo, aliases.Single(a => a.Name == "Romashka MChJ").Source);
    }

    [Fact]
    public async Task Orders_by_similarity_and_shows_similar_names_lower()
    {
        RequirePostgres();
        var items = (await Find("Ромашка")).Candidates.Items;
        Assert.Equal(CounterpartySearchFixture.Romashka, items[0].Inn);
        var second = items.Single(i => i.Inn == CounterpartySearchFixture.Romashkino);
        Assert.True(items[0].Score > second.Score);
        Assert.Equal(items.OrderByDescending(i => i.Score).ThenByDescending(i => i.Activity).Select(i => i.Inn), items.Select(i => i.Inn));
    }

    [Theory]
    [InlineData("Rassvet", CounterpartySearchFixture.Rassvet)]
    [InlineData("рассвет", CounterpartySearchFixture.Rassvet)]
    [InlineData("O'zbekiston temir yo'llari", CounterpartySearchFixture.Railways)]
    [InlineData("Узбекистон темир йуллари", CounterpartySearchFixture.Railways)]
    [InlineData("temir yollari AJ", CounterpartySearchFixture.Railways)]
    [InlineData("Каримов Жасур ЯТТ", CounterpartySearchFixture.Pinfl)]
    public async Task Finds_russian_and_uzbek_names_across_scripts(string query, string inn)
    {
        RequirePostgres();
        Assert.Equal(inn, (await Find(query)).Candidates.Items[0].Inn);
    }

    [Theory]
    [InlineData(CounterpartySearchFixture.Romashka)]
    [InlineData(CounterpartySearchFixture.Pinfl)]
    public async Task An_inn_finds_exactly_that_counterparty(string inn)
    {
        RequirePostgres();
        var r = await Find(inn);
        Assert.True(r.IsInnQuery);
        var c = Assert.Single(r.Candidates.Items);
        Assert.Equal(inn, c.Inn);
        Assert.Equal(1m, c.Score);
    }

    [Fact]
    public async Task Unknown_names_find_nothing()
    {
        RequirePostgres();
        Assert.Empty((await Find("Зебра Логистик")).Candidates.Items);
        Assert.Empty((await Find("999999999")).Candidates.Items);
    }

    [Fact]
    public async Task Payments_without_an_inn_are_not_found_but_counted()
    {
        RequirePostgres();
        var r = await Find("Ромашка");
        Assert.Equal(2, r.PaymentsWithoutInn);
        Assert.DoesNotContain(r.Candidates.Items, c => string.IsNullOrEmpty(c.Inn));
    }

    [Fact]
    public async Task Tenants_see_only_their_own_counterparties_and_amounts()
    {
        RequirePostgres();
        var t = Assert.Single((await Find("Ромашка", f.TenantT)).Candidates.Items);
        Assert.Equal(CounterpartySearchFixture.Romashka, t.Inn);
        Assert.Equal(777_00, Assert.Single(t.BankTurnover).Outgoing);
        Assert.Empty(t.OtherNames);
        Assert.Empty((await Find("Rassvet", f.TenantT)).Candidates.Items);
        Assert.Equal(0, (await Find("Ромашка", f.TenantT)).PaymentsWithoutInn);
    }

    [Fact]
    public async Task Pages_through_many_candidates_without_repeats()
    {
        RequirePostgres();
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var r = await Find("Тест Компания", limit: 25, cursor: cursor);
            seen.AddRange(r.Candidates.Items.Select(c => c.Inn));
            cursor = r.Candidates.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_queries_are_rejected(string query)
    {
        RequirePostgres();
        await Assert.ThrowsAsync<ArgumentException>(() => f.FindAsync(f.TenantS, query));
    }

    [Fact]
    public async Task Loads_record_name_spellings_as_they_go()
    {
        RequirePostgres();
        var tenant = await f.Host.CreateTenantAsync("Search sync");
        var bank = await f.Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var edo = await f.Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        await f.Host.SyncBankAsync(tenant, bank);
        await f.Host.SyncEdoAsync(tenant, edo);

        var (aliases, bankNames, edoNames) = await f.Host.InTenantAsync(tenant, sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(async ct => (
                await db.CounterpartyAliases.ToListAsync(ct),
                await db.BankTransactions.Where(t => t.CounterpartyInn != null && t.CounterpartyInn != "" && t.CounterpartyName != null)
                    .GroupBy(t => new { t.CounterpartyInn, t.CounterpartyName }).Select(g => new { g.Key.CounterpartyInn, g.Key.CounterpartyName, N = g.Count() }).ToListAsync(ct),
                await db.EdoDocuments.Where(d => d.CounterpartyInn != null && d.CounterpartyName != null)
                    .GroupBy(d => new { d.CounterpartyInn, d.CounterpartyName }).Select(g => new { g.Key.CounterpartyInn, g.Key.CounterpartyName, N = g.Count() }).ToListAsync(ct)),
                CancellationToken.None);
        });
        Assert.NotEmpty(bankNames);
        Assert.NotEmpty(edoNames);
        foreach (var b in bankNames)
            Assert.Equal(b.N, aliases.Single(a => a.Source == CounterpartyNameSource.Bank && a.CounterpartyInn == b.CounterpartyInn && a.Name == b.CounterpartyName!.Trim()).Occurrences);
        foreach (var e in edoNames)
            Assert.Equal(e.N, aliases.Single(a => a.Source == CounterpartyNameSource.Edo && a.CounterpartyInn == e.CounterpartyInn && a.Name == e.CounterpartyName!.Trim()).Occurrences);

        // A repeated load of the same range adds nothing; the rebuild gives the same rows.
        f.Host.Time.Advance(TimeSpan.FromMinutes(5));
        await f.Host.SyncBankAsync(tenant, bank);
        var before = aliases.Select(a => (a.CounterpartyInn, a.Source, a.Name, a.Occurrences)).Order().ToList();
        await f.RebuildAsync(tenant);
        var after = await f.Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ISynectDbContext>()
            .InTransactionAsync(ct => sp.GetRequiredService<ISynectDbContext>().CounterpartyAliases.ToListAsync(ct), CancellationToken.None));
        Assert.Equal(before, after.Select(a => (a.CounterpartyInn, a.Source, a.Name, a.Occurrences)).Order().ToList());
    }
}

/// <summary>The EF path of alias recording (SQLite, used by the logic tests of the loads).</summary>
public class CounterpartyAliasSqliteTests
{
    [Fact]
    public async Task Bank_load_records_aliases_on_sqlite_too()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var tenant = await host.CreateTenantAsync("sqlite aliases");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.SyncBankAsync(tenant, bank);
        var count = await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ISynectDbContext>()
            .InTransactionAsync(ct => sp.GetRequiredService<ISynectDbContext>().CounterpartyAliases.CountAsync(ct), CancellationToken.None));
        Assert.True(count > 0);
    }
}
