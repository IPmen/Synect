using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Statements;
using Synect.Application.Sync;
using Synect.Domain.Banking;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Statements;

/// <summary>
/// The two write paths of <see cref="BankLoadWriter"/>: EF entities (SQLite, and forced here) and bulk
/// INSERT ... SELECT FROM unnest(...) ON CONFLICT on PostgreSQL. Same files, same clock: the stored data must be identical.
/// Also row-level security of the bulk statements under the restricted application role.
/// </summary>
public class StatementBulkWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;
    private static readonly DateTimeOffset T0 = StatementKit.Now;

    private Task<StatementImportResult> ImportAsync(bool bulk, Guid tenant, Guid connection, byte[] content, string file, StatementProfile? profile = null) =>
        Host.InTenantAsync(tenant, sp =>
        {
            sp.GetRequiredService<BankLoadWriter>().BulkWrites = bulk;
            return sp.GetRequiredService<BankStatementImportService>().ImportAsync(
                new StatementImportRequest(connection, file, StatementFormat.Mt940, profile ?? StatementProfiles.GenericMt940, CreateAccounts: true),
                new MemoryStream(content), CancellationToken.None);
        });

    /// <summary>Everything an import writes, without ids that differ by construction (tenant, connection, row ids).</summary>
    private Task<List<string>> SnapshotAsync(Guid tenant) => Host.QueryAsync(tenant, async db =>
    {
        var lines = new List<string>();
        var accounts = await db.BankAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => $"{a.AccountNumber}|{a.Mfo}|{a.Currency}");
        lines.AddRange(accounts.Values.Order(StringComparer.Ordinal).Select(a => "account " + a));
        lines.AddRange((await db.BankTransactions.AsNoTracking().ToListAsync())
            .Select(t => string.Join("|", "op", accounts[t.BankAccountId], t.OperationId, t.Amount, t.Currency, t.Direction, t.CounterpartyInn,
                t.CounterpartyName, t.CounterpartyAccount, t.CounterpartyMfo, t.Purpose, t.PurposeCode, t.DocumentNumber, t.PostingDate, t.ValueDate,
                t.IsFinal, t.SourceState, t.HasError, t.ErrorCode, t.ErrorMessage, t.IdSource, t.Details, t.SourceHash, t.CreatedAt.UtcTicks, t.UpdatedAt.UtcTicks))
            .Order(StringComparer.Ordinal));
        // last_seen_at differs by design: the EF path refreshes it for unchanged objects, statement imports on the bulk path do not.
        lines.AddRange((await db.RawObjects.AsNoTracking().ToListAsync())
            .Select(r => string.Join("|", "raw", r.SourceCode, r.ObjectType, r.ExternalId, r.Hash, r.Payload, r.FirstSeenAt.UtcTicks, r.UpdatedAt.UtcTicks))
            .Order(StringComparer.Ordinal));
        lines.AddRange((await db.BankDailyBalances.AsNoTracking().ToListAsync())
            .Select(b => string.Join("|", "day", accounts[b.BankAccountId], b.Date, b.OpeningBalance, b.ClosingBalance, b.TotalDebit, b.TotalCredit, b.IsDayClosed))
            .Order(StringComparer.Ordinal));
        lines.AddRange((await db.Counterparties.AsNoTracking().ToListAsync()).Select(c => $"cp|{c.Inn}|{c.Name}").Order(StringComparer.Ordinal));
        lines.AddRange((await db.CounterpartyAliases.AsNoTracking().ToListAsync())
            .Select(a => string.Join("|", "alias", a.CounterpartyInn, a.Source, a.Name, a.NormalizedName, a.Occurrences, a.FirstSeenAt.UtcTicks, a.LastSeenAt.UtcTicks))
            .Order(StringComparer.Ordinal));
        lines.AddRange((await db.CounterpartyMonthly.AsNoTracking().ToListAsync())
            .Select(m => string.Join("|", "month", m.CounterpartyInn, m.Month, m.Currency, m.BankIncoming, m.BankOutgoing, m.BankCount))
            .Order(StringComparer.Ordinal));
        lines.AddRange((await db.StatementImports.AsNoTracking().ToListAsync())
            .Select(i => string.Join("|", "import", i.FileName, i.FileSha256, i.Account, i.PeriodFrom, i.PeriodTo, i.TransactionsTotal, i.TransactionsNew,
                i.TransactionsDuplicate, i.RowsRejected, i.BalanceCheck))
            .Order(StringComparer.Ordinal));
        lines.Add("matches " + await db.ReconciliationMatches.CountAsync());
        lines.Add("findings " + await db.ReconciliationFindings.CountAsync());
        return lines;
    });

    [Fact]
    public async Task Ef_and_bulk_paths_store_identical_data_for_the_same_files()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var reference = StatementProfiles.KapitalBankMt940Assumed;
        reference.OperationId = "bank_reference";
        var multi = StatementFiles.Read("mt940_multi_cp1251.sta");
        var kapital = StatementFiles.Read("mt940_kapitalbank_assumed.sta");
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(kapital).Replace("Оплата по договору 15/2026", "Оплата по договору 15/2026 (уточнено)"));
        (string File, byte[] Content, StatementProfile? Profile)[] steps =
        [
            ("multi.sta", multi, null),                                 // new operations, two accounts, reversal, PINFL
            ("multi-copy.sta", [.. multi, 13, 10], null),               // the same operations in another file: all duplicates
            ("uzbek.sta", StatementFiles.Read("mt940_uzbek_utf8bom.sta"), null),
            ("kapital.sta", kapital, reference),                        // file ids
            ("kapital-changed.sta", changed, reference),                // same ids, changed content: ON CONFLICT update
        ];

        var results = new Dictionary<bool, List<StatementImportResult>>();
        var snapshots = new Dictionary<bool, List<string>>();
        foreach (var bulk in new[] { false, true })
        {
            var (tenant, connection) = await Host.FileTenantAsync(bulk ? "Write path bulk" : "Write path EF");
            results[bulk] = [];
            for (var i = 0; i < steps.Length; i++)
            {
                Host.Time.Set(T0.AddMinutes(i)); // the same clock for both paths
                results[bulk].Add(await ImportAsync(bulk, tenant, connection, steps[i].Content, steps[i].File, steps[i].Profile));
            }
            snapshots[bulk] = await SnapshotAsync(tenant);
        }

        Assert.All(results[true], r => Assert.Equal(StatementImportStatus.Imported, r.Status));
        Assert.Equal(results[false].Select(r => (r.TransactionsTotal, r.TransactionsNew, r.TransactionsDuplicate, r.BalanceCheck)),
            results[true].Select(r => (r.TransactionsTotal, r.TransactionsNew, r.TransactionsDuplicate, r.BalanceCheck)));
        Assert.Equal((5, 0, 5), (results[true][1].TransactionsTotal, results[true][1].TransactionsNew, results[true][1].TransactionsDuplicate));
        Assert.Contains(snapshots[true], l => l.StartsWith("op|") && l.Contains("(уточнено)"));
        Assert.True(snapshots[true].Count(l => l.StartsWith("op|")) == 10, string.Join("\n", snapshots[true].Where(l => l.StartsWith("op|"))));
        Assert.Equal(snapshots[false], snapshots[true]);
    }

    [Fact]
    public async Task Bulk_statements_run_under_the_restricted_role_and_row_level_security_rejects_other_tenants()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        Host.Time.Set(T0);
        var (a, connectionA) = await Host.FileTenantAsync("Bulk RLS A");
        var (b, connectionB) = await Host.FileTenantAsync("Bulk RLS B");
        Assert.Equal(StatementImportStatus.Imported, (await ImportAsync(true, b, connectionB, StatementFiles.Read("mt940_single_utf8.sta"), "b.sta")).Status);
        var accountB = await Host.QueryAsync(b, db => db.BankAccounts.AsNoTracking().SingleAsync());
        var rowB = await Host.QueryAsync(b, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.OperationId).FirstAsync());
        var rawB = await Host.CountAsync(b, db => db.RawObjects);

        // The application session is the restricted role: not superuser, not owner, no BYPASSRLS.
        var role = await Host.QueryAsync(a, db => db.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync());
        Assert.Equal(fixture.AppRole, role);
        Assert.Equal("false,false", await fixture.ScalarAsync<string>($"SELECT rolsuper::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = '{fixture.AppRole}'"));

        // 1. Raw layer: rows that claim tenant B, written in tenant A's transaction, violate the policy (WITH CHECK).
        var connectionAsB = await Host.QueryAsync(b, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == connectionB));
        var raw = await Assert.ThrowsAnyAsync<Exception>(() => Host.InTenantAsync(a, sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(ct => sp.GetRequiredService<RawObjectStore>().UpsertBulkAsync(connectionAsB,
                [new RawRecord("statement.transaction", "intruder", "{\"x\":1}")], T0, ct), CancellationToken.None);
        }));
        Assert.Equal("42501", SqlState(raw)); // insufficient_privilege: new row violates row-level security policy

        // 2. Operations: tenant B's account and operation id, written through the bulk upsert in tenant A's context.
        var info = new BankTransactionInfo(rowB.OperationId, 1, "UZS", TransactionDirection.Incoming, null, null, null, null, "overwrite attempt", null, null,
            rowB.PostingDate, null, null, null, null, new RawRecord("statement.transaction", "intruder-op", "{\"y\":2}"));
        var asA = await Host.QueryAsync(a, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == connectionA));
        var ops = await Assert.ThrowsAnyAsync<Exception>(() => Host.InTenantAsync(a, sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var writer = sp.GetRequiredService<BankLoadWriter>();
            writer.BulkWrites = true;
            return db.InTransactionAsync(ct => writer.UpsertTransactionsAsync(asA, accountB, [info], dayClosed: true, ct), CancellationToken.None);
        }));
        // Tenant A cannot reference B's account: the composite foreign key (tenant_id, bank_account_id) does not exist for A.
        Assert.Contains(SqlState(ops), new[] { "23503", "42501" });
        // As B's row claims: tenant B, in tenant A's context the bulk upsert is refused by RLS too.
        var asB = await Assert.ThrowsAnyAsync<Exception>(() => Host.InTenantAsync(a, sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var writer = sp.GetRequiredService<BankLoadWriter>();
            writer.BulkWrites = true;
            return db.InTransactionAsync(ct => writer.UpsertTransactionsAsync(connectionAsB, accountB, [info], dayClosed: true, ct), CancellationToken.None);
        }));
        Assert.Equal("42501", SqlState(asB));

        // Nothing of B changed, nothing leaked into A.
        var after = await Host.QueryAsync(b, db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == rowB.Id));
        Assert.Equal((rowB.Amount, rowB.Purpose, rowB.SourceHash, rowB.UpdatedAt), (after.Amount, after.Purpose, after.SourceHash, after.UpdatedAt));
        Assert.Equal(rawB, await Host.CountAsync(b, db => db.RawObjects));
        Assert.Equal(0, await Host.CountAsync(a, db => db.BankTransactions));
        Assert.Equal(0, await Host.CountAsync(a, db => db.RawObjects));
    }

    private static string? SqlState(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is DbException { SqlState: { } state }) return state;
        return null;
    }
}
