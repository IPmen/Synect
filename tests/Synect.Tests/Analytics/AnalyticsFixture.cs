using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Analytics.Totals;
using Synect.Domain.Banking;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Analytics;

/// <summary>
/// A small deterministic data set on a throwaway PostgreSQL database (see PostgresFixture), written directly
/// into the tables so that every expected number in the tests can be computed by hand.
/// Test clock: 2026-11-07 10:00 UTC (15:00 in Tashkent).
/// </summary>
public sealed class AnalyticsFixture : IAsyncLifetime
{
    public const string Alpha = "301000001";
    public const string Beta = "302000002";
    public const string Gamma = "303000003";
    public const string Epsilon = "304000004";
    public const string Delta = "305000005";

    public static readonly DateOnly Oct1 = new(2026, 10, 1);

    private readonly PostgresFixture _pg = new();

    public bool Available => _pg.Available;
    public PostgresFixture Postgres => _pg;
    public TestHost Host => _pg.Host;
    public Guid TenantA { get; private set; }
    public Guid TenantB { get; private set; }
    public Guid UzsAccount { get; private set; }
    public Guid UsdAccount { get; private set; }
    public Guid IdleAccount { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;

        TenantA = await Host.CreateTenantAsync("Analytics A");
        TenantB = await Host.CreateTenantAsync("Analytics B");
        await SeedTenantAAsync();
        await SeedTenantBAsync();
    }

    public ValueTask DisposeAsync() => _pg.DisposeAsync();

    private async Task SeedTenantAAsync()
    {
        var bank = await Host.CreateConnectionAsync(TenantA, AdapterKind.Bank);
        var edo = await Host.CreateConnectionAsync(TenantA, AdapterKind.Edo);
        await WriteAsync(TenantA, db =>
        {
            var uzs = Account(bank, "20208000100000000001", "UZS", "Main UZS");
            var usd = Account(bank, "20208840100000000002", "USD", "USD account");
            var idle = Account(bank, "20208000100000000003", "UZS", "Old UZS");
            db.BankAccounts.AddRange(uzs, usd, idle);
            UzsAccount = uzs.Id; UsdAccount = usd.Id; IdleAccount = idle.Id;

            db.BankTransactions.AddRange(
                Tx(bank, uzs, "T1", new(2026, 10, 1), TransactionDirection.Incoming, 100_000, Alpha, "Alpha LLC"),
                Tx(bank, uzs, "T2", new(2026, 10, 1), TransactionDirection.Outgoing, 30_000, Beta, "Beta LLC"),
                Tx(bank, uzs, "T3", new(2026, 10, 2), TransactionDirection.Incoming, 50_000, Alpha, "Alpha LLC"),
                Tx(bank, uzs, "T4", new(2026, 10, 2), TransactionDirection.Outgoing, 20_000, null, "Bank fee"),
                Tx(bank, uzs, "T5", new(2026, 10, 2), TransactionDirection.Outgoing, 999_999, Beta, "Beta LLC", hasError: true),
                Tx(bank, uzs, "T6", new(2026, 10, 3), TransactionDirection.Incoming, 70_000, Gamma, "Gamma LLC"),
                Tx(bank, uzs, "T7", new(2026, 10, 3), TransactionDirection.Outgoing, 10_000, Delta, "Delta LLC"),
                Tx(bank, usd, "T8", new(2026, 10, 1), TransactionDirection.Incoming, 1_000, Alpha, "Alpha LLC"),
                Tx(bank, uzs, "T9", new(2026, 10, 31), TransactionDirection.Incoming, 5_000, Alpha, "Alpha LLC"),
                Tx(bank, uzs, "T10", new(2026, 11, 1), TransactionDirection.Incoming, 7_000, Gamma, "Gamma LLC"),
                Tx(bank, uzs, "T11", new(2026, 9, 15), TransactionDirection.Incoming, 40_000, Epsilon, "Epsilon LLC"));

            db.BankDailyBalances.AddRange(
                Balance(uzs, new(2026, 10, 1), 1_000_000, 100_000, 30_000, 1_070_000, closed: true),
                Balance(uzs, new(2026, 10, 2), 1_070_000, 50_000, 20_000, 1_100_000, closed: true),
                Balance(uzs, new(2026, 10, 3), 1_100_000, 70_000, 10_000, 1_160_000, closed: false),
                Balance(usd, new(2026, 10, 1), 5_000, 1_000, 0, 6_000, closed: true),
                Balance(usd, new(2026, 10, 2), 6_000, 500, 0, 6_500, closed: true),   // no operations: totals mismatch
                Balance(idle, new(2025, 1, 10), 500, 0, 0, 600, closed: true),       // day equation broken
                Balance(idle, new(2025, 1, 15), 700, 0, 0, 700, closed: true));      // carry-over broken, 4 days missing

            db.Counterparties.AddRange(
                Party(Alpha, "Alpha LLC"), Party(Beta, "Beta LLC"), Party(Gamma, "Gamma LLC"),
                Party(Epsilon, "Epsilon LLC"), Party(Delta, "Delta LLC"));

            db.EdoDocuments.AddRange(
                Doc(edo, "E1", EdoDocumentOwner.Incoming, "2", "005", "B-17", new(2026, 10, 2), Beta, "Beta LLC", 30_000, 3_214,
                    created: At(2026, 10, 2, 5)),
                Doc(edo, "E2", EdoDocumentOwner.Outgoing, "1", "002", "INV-001", new(2026, 9, 20), Alpha, "Alpha LLC", 150_000, 16_071,
                    created: At(2026, 9, 20, 5), contract: "C-1"),
                Doc(edo, "E3", EdoDocumentOwner.Outgoing, "3", "002", "INV-002", new(2026, 10, 3), Alpha, "Alpha LLC", 200_000, 21_428,
                    created: At(2026, 10, 3, 5), contract: "C-1"),
                Doc(edo, "E4", EdoDocumentOwner.Outgoing, "4", "002", "INV-003", new(2026, 10, 4), Gamma, "Gamma LLC", 99_000, 0,
                    created: At(2026, 10, 4, 5), comment: "Wrong amount"),
                Doc(edo, "E5", EdoDocumentOwner.Incoming, "0", "005", "D-1", new(2026, 10, 5), Epsilon, "Epsilon LLC", 5_000, 0,
                    created: At(2026, 10, 5, 5)),
                // 2026-10-31 20:00 UTC is 2026-11-01 01:00 in Tashkent.
                Doc(edo, "E6", EdoDocumentOwner.Outgoing, "3", "002", "INV-004", new(2026, 10, 31), Alpha, "Alpha LLC", 11_000, 1_178,
                    created: At(2026, 10, 31, 20)));
        });
        await RebuildTotalsAsync(TenantA);
    }

    private async Task SeedTenantBAsync()
    {
        var bank = await Host.CreateConnectionAsync(TenantB, AdapterKind.Bank);
        var edo = await Host.CreateConnectionAsync(TenantB, AdapterKind.Edo);
        await WriteAsync(TenantB, db =>
        {
            var uzs = Account(bank, "20208000200000000001", "UZS", "B main");
            db.BankAccounts.Add(uzs);
            db.BankTransactions.Add(Tx(bank, uzs, "B1", new(2026, 10, 1), TransactionDirection.Incoming, 9_999, Alpha, "Alpha LLC"));
            db.BankDailyBalances.Add(Balance(uzs, new(2026, 10, 1), 1, 9_999, 0, 10_000, closed: true));
            db.EdoDocuments.Add(Doc(edo, "BE1", EdoDocumentOwner.Outgoing, "3", "002", "INV-001", new(2026, 10, 3), Alpha, "Alpha LLC",
                77_000, 8_250, created: At(2026, 10, 3, 5)));
        });
        await RebuildTotalsAsync(TenantB);
    }

    private Task RebuildTotalsAsync(Guid tenant) => Host.InTenantAsync(tenant, async sp =>
    {
        var db = sp.GetRequiredService<ISynectDbContext>();
        return await db.InTransactionAsync(ct => sp.GetRequiredService<CounterpartyMonthlyRefresher>().RebuildAllAsync(ct), CancellationToken.None);
    });

    private Task WriteAsync(Guid tenant, Action<ISynectDbContext> write) => Host.InTenantAsync(tenant, async sp =>
    {
        var db = sp.GetRequiredService<ISynectDbContext>();
        await db.InTransactionAsync(async ct =>
        {
            write(db);
            await db.SaveChangesAsync(ct);
        }, CancellationToken.None);
        return true;
    });

    private DateTimeOffset Now => Host.Time.Now;

    private static DateTimeOffset At(int y, int m, int d, int hourUtc) => new(y, m, d, hourUtc, 0, 0, TimeSpan.Zero);

    private BankAccount Account(Guid connection, string number, string currency, string name) => new()
    {
        ConnectionId = connection, AccountNumber = number, Mfo = "00974", Currency = currency, Name = name,
        CreatedAt = Now, UpdatedAt = Now,
    };

    private BankTransaction Tx(Guid connection, BankAccount account, string id, DateOnly date, TransactionDirection direction,
        long amount, string? inn, string name, bool hasError = false) => new()
    {
        ConnectionId = connection, BankAccountId = account.Id, OperationId = id, Amount = amount, Currency = account.Currency,
        Direction = direction, CounterpartyInn = inn, CounterpartyName = name, CounterpartyAccount = "20208000999999999999",
        CounterpartyMfo = "00444", Purpose = "Payment " + id, DocumentNumber = id, PostingDate = date, ValueDate = date,
        IsFinal = true, SourceState = hasError ? "9" : "3", HasError = hasError, ErrorCode = hasError ? 5 : null,
        ErrorMessage = hasError ? "Insufficient funds" : null, SourceHash = new string('0', 64), CreatedAt = Now, UpdatedAt = Now,
    };

    private BankDailyBalance Balance(BankAccount account, DateOnly date, long opening, long credit, long debit, long closing, bool closed) => new()
    {
        BankAccountId = account.Id, Date = date, OpeningBalance = opening, TotalCredit = credit, TotalDebit = debit,
        ClosingBalance = closing, IsDayClosed = closed, UpdatedAt = Now,
    };

    private Counterparty Party(string inn, string name) => new() { Inn = inn, Name = name, CreatedAt = Now, UpdatedAt = Now };

    private EdoDocument Doc(Guid connection, string externalId, EdoDocumentOwner owner, string status, string docType, string number,
        DateOnly date, string inn, string name, long total, long vat, DateTimeOffset created, string? contract = null, string? comment = null)
    {
        var doc = new EdoDocument
        {
            ConnectionId = connection, ExternalId = externalId, DocType = docType, Owner = owner, Number = number,
            DocumentDate = date, CounterpartyInn = inn, CounterpartyName = name, ContractNumber = contract,
            TotalSum = total, TotalVatSum = vat, TotalDeliverySum = total - vat, TotalDeliverySumWithVat = total,
            StatusComment = comment, SourceCreatedAt = created, SourceUpdatedAt = created, SourceHash = new string('0', 64),
            CreatedAt = Now, UpdatedAt = Now,
        };
        doc.ApplyStatus(status, Now, null);
        return doc;
    }
}
