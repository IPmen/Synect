using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Analytics.Counterparties;
using Synect.Application.Reconciliation;
using Synect.Domain.Reconciliation;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;
using static Synect.Infrastructure.Adapters.Fake.ReconciliationGoldenSet;

namespace Synect.Tests.Reconciliation;

/// <summary>Recalculation over the database (SQLite): idempotency, decisions, incremental scope, one Proposed match per item.</summary>
public class ReconciliationServiceTests
{
    private static Task<ReconciliationRunResult> ReconcileAsync(TestHost host, Guid tenant, ReconciliationScope scope) =>
        host.InTenantAsync(tenant, sp => sp.GetRequiredService<ReconciliationService>().ReconcileAsync(scope, CancellationToken.None));

    private static Task<List<ReconciliationMatch>> MatchesAsync(TestHost host, Guid tenant) =>
        host.QueryAsync(tenant, db => db.ReconciliationMatches.AsNoTracking().OrderBy(m => m.Id).ToListAsync());

    [Fact]
    public async Task Recalculation_is_idempotent_and_keeps_match_ids()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        var before = await MatchesAsync(host, tenant);
        var findingsBefore = await host.CountAsync(tenant, db => db.ReconciliationFindings);

        var full = await ReconcileAsync(host, tenant, ReconciliationScope.Full);
        Assert.Equal((0, 0, before.Count), (full.Created, full.Removed, full.Kept));
        var again = await ReconcileAsync(host, tenant, ReconciliationScope.Full);
        Assert.Equal((0, 0), (again.Created, again.Removed));

        var after = await MatchesAsync(host, tenant);
        Assert.Equal(before.Select(m => (m.Id, m.Signature, m.Confidence, m.Reasons)), after.Select(m => (m.Id, m.Signature, m.Confidence, m.Reasons)));
        Assert.Equal(findingsBefore, await host.CountAsync(tenant, db => db.ReconciliationFindings));
    }

    [Fact]
    public async Task Confirmed_and_rejected_matches_survive_every_recalculation()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        var matches = await MatchesAsync(host, tenant);
        var confirmed = matches.Single(m => m.CounterpartyInn == C && m.MatchType == ReconciliationMatchType.Partial); // overpaid 47
        var rejected = matches.Single(m => m.CounterpartyInn == E);
        var keyId = Guid.NewGuid();
        await host.InTenantAsync(tenant, async sp =>
        {
            var review = sp.GetRequiredService<ReconciliationReviewService>();
            Assert.Equal(ReviewOutcome.Confirmed, (await review.ConfirmAsync(confirmed.Id, keyId, "ok", CancellationToken.None)).Outcome);
            Assert.Equal(ReviewOutcome.Rejected, (await review.RejectAsync(rejected.Id, keyId, null, CancellationToken.None)).Outcome);
            Assert.Equal(ReviewOutcome.Unchanged, (await review.ConfirmAsync(confirmed.Id, keyId, "again", CancellationToken.None)).Outcome);
            Assert.Equal(ReviewOutcome.NotFound, (await review.ConfirmAsync(Guid.NewGuid(), keyId, null, CancellationToken.None)).Outcome);
            return true;
        });

        await ReconcileAsync(host, tenant, ReconciliationScope.Full);
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ReconciliationService>().RebuildAsync(CancellationToken.None));
        await ReconcileAsync(host, tenant, ReconciliationScope.ForInns([C, E]));

        var after = await MatchesAsync(host, tenant);
        var c = after.Single(m => m.Id == confirmed.Id);
        Assert.Equal((MatchStatus.Confirmed, keyId, "ok"), (c.Status, c.DecidedByKey!.Value, c.DecisionComment));
        Assert.Equal(MatchStatus.Rejected, after.Single(m => m.Id == rejected.Id).Status);
        // The rejected pair is not proposed again, and the confirmed items are not proposed a second time.
        Assert.DoesNotContain(after, m => m.CounterpartyInn == E && m.Status == MatchStatus.Proposed);
        var items = await host.QueryAsync(tenant, db => db.ReconciliationMatchItems.AsNoTracking().ToListAsync());
        var confirmedRefs = items.Where(i => i.MatchId == confirmed.Id).Select(i => i.RefId).ToHashSet();
        Assert.DoesNotContain(items, i => i.MatchStatus == MatchStatus.Proposed && confirmedRefs.Contains(i.RefId));
        Assert.All(items.Where(i => i.MatchId == rejected.Id), i => Assert.Equal(MatchStatus.Rejected, i.MatchStatus));
    }

    private static Task<ReviewResult> DecideAsync(TestHost host, Guid tenant, Guid matchId, bool confirm, Guid key, string? comment) =>
        host.InTenantAsync(tenant, sp => confirm
            ? sp.GetRequiredService<ReconciliationReviewService>().ConfirmAsync(matchId, key, comment, CancellationToken.None)
            : sp.GetRequiredService<ReconciliationReviewService>().RejectAsync(matchId, key, comment, CancellationToken.None));

    private static async Task RecalculateEverythingAsync(TestHost host, Guid tenant)
    {
        await ReconcileAsync(host, tenant, ReconciliationScope.Full);
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ReconciliationService>().RebuildAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Decisions_can_be_changed_both_ways_and_every_change_is_kept_in_history()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        var match = (await MatchesAsync(host, tenant)).Single(m => m.CounterpartyInn == C && m.MatchType == ReconciliationMatchType.Partial);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Equal(ReviewOutcome.Confirmed, (await DecideAsync(host, tenant, match.Id, confirm: true, first, "ok")).Outcome);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var rejected = await DecideAsync(host, tenant, match.Id, confirm: false, second, "wrong invoice");
        Assert.Equal((ReviewOutcome.Rejected, MatchStatus.Rejected, MatchStatus.Confirmed), (rejected.Outcome, rejected.Status, rejected.PreviousStatus));
        Assert.NotNull(rejected.DecisionId);

        // Recalculation keeps the Rejected match and does not propose its pairs again.
        await RecalculateEverythingAsync(host, tenant);
        var items = await host.QueryAsync(tenant, db => db.ReconciliationMatchItems.AsNoTracking().ToListAsync());
        var own = items.Where(i => i.MatchId == match.Id).ToList();
        Assert.All(own, i => Assert.Equal(MatchStatus.Rejected, i.MatchStatus));
        var pairs = own.Where(i => i.Kind == MatchItemKind.BankTransaction)
            .SelectMany(p => own.Where(i => i.Kind == MatchItemKind.EdoDocument).Select(d => (p.RefId, d.RefId))).ToHashSet();
        foreach (var g in items.Where(i => i.MatchStatus == MatchStatus.Proposed).GroupBy(i => i.MatchId))
            foreach (var p in g.Where(i => i.Kind == MatchItemKind.BankTransaction))
                foreach (var d in g.Where(i => i.Kind == MatchItemKind.EdoDocument))
                    Assert.DoesNotContain((p.RefId, d.RefId), pairs);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        var back = await DecideAsync(host, tenant, match.Id, confirm: true, first, "checked again");
        Assert.Equal((ReviewOutcome.Confirmed, MatchStatus.Rejected), (back.Outcome, back.PreviousStatus));
        Assert.Equal(ReviewOutcome.Unchanged, (await DecideAsync(host, tenant, match.Id, confirm: true, first, null)).Outcome);

        // Recalculation keeps the Confirmed match, and its items are not proposed a second time.
        await RecalculateEverythingAsync(host, tenant);
        var stored = (await MatchesAsync(host, tenant)).Single(m => m.Id == match.Id);
        Assert.Equal((MatchStatus.Confirmed, first, "checked again"), (stored.Status, stored.DecidedByKey!.Value, stored.DecisionComment));
        items = await host.QueryAsync(tenant, db => db.ReconciliationMatchItems.AsNoTracking().ToListAsync());
        var refs = items.Where(i => i.MatchId == match.Id).Select(i => i.RefId).ToHashSet();
        Assert.All(items.Where(i => i.MatchId == match.Id), i => Assert.Equal(MatchStatus.Confirmed, i.MatchStatus));
        Assert.DoesNotContain(items, i => i.MatchStatus == MatchStatus.Proposed && refs.Contains(i.RefId));

        var history = await host.QueryAsync(tenant, db => db.ReconciliationMatchDecisions.AsNoTracking()
            .Where(d => d.MatchId == match.Id).OrderBy(d => d.DecidedAt).ToListAsync());
        Assert.Equal(
            [(MatchStatus.Proposed, MatchStatus.Confirmed, first, "ok"),
             (MatchStatus.Confirmed, MatchStatus.Rejected, second, "wrong invoice"),
             (MatchStatus.Rejected, MatchStatus.Confirmed, first, "checked again")],
            history.Select(h => (h.PreviousStatus, h.NewStatus, h.DecidedByKey!.Value, h.Comment!)));
        Assert.Equal(rejected.DecisionId, history[1].Id);
        Assert.True(history[0].DecidedAt < history[1].DecidedAt && history[1].DecidedAt < history[2].DecidedAt);
    }

    [Fact]
    public async Task A_rejected_match_whose_payment_is_used_by_another_confirmed_match_cannot_be_confirmed()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        var match = (await MatchesAsync(host, tenant)).Single(m => m.CounterpartyInn == E);
        var key = Guid.NewGuid();
        Assert.Equal(ReviewOutcome.Rejected, (await DecideAsync(host, tenant, match.Id, confirm: false, key, null)).Outcome);

        // Meanwhile the same payment was confirmed in full for another document.
        var payment = await host.QueryAsync(tenant, db => db.ReconciliationMatchItems.AsNoTracking()
            .SingleAsync(i => i.MatchId == match.Id && i.Kind == MatchItemKind.BankTransaction));
        var amount = await host.QueryAsync(tenant, db => db.BankTransactions.Where(t => t.Id == payment.RefId).Select(t => t.Amount).SingleAsync());
        var other = await host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var m = new ReconciliationMatch
            {
                RunId = match.RunId, MatchType = ReconciliationMatchType.Exact, Status = MatchStatus.Confirmed, Confidence = 100,
                CounterpartyInn = E, Currency = match.Currency, DocumentOwner = match.DocumentOwner, PaymentsTotal = amount, DocumentsTotal = amount,
                Signature = "manual", CreatedAt = host.Time.Now, UpdatedAt = host.Time.Now, DecidedAt = host.Time.Now, DecidedByKey = key,
            };
            await db.InTransactionAsync(async ct =>
            {
                db.ReconciliationMatches.Add(m);
                db.ReconciliationMatchItems.Add(new ReconciliationMatchItem
                {
                    MatchId = m.Id, Kind = MatchItemKind.BankTransaction, RefId = payment.RefId, AllocatedAmountMinor = amount,
                    MatchStatus = MatchStatus.Confirmed,
                });
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return m.Id;
        });

        var result = await DecideAsync(host, tenant, match.Id, confirm: true, key, null);
        Assert.Equal(ReviewOutcome.Conflict, result.Outcome);
        Assert.Equal([other], result.ConflictingMatches!);
        Assert.Equal(MatchStatus.Rejected, (await MatchesAsync(host, tenant)).Single(m => m.Id == match.Id).Status);
        Assert.Equal(1, await host.CountAsync(tenant, db => db.ReconciliationMatchDecisions.Where(d => d.MatchId == match.Id)));

        // Once the other match is rejected, this one can be confirmed.
        Assert.Equal(ReviewOutcome.Rejected, (await DecideAsync(host, tenant, other, confirm: false, key, null)).Outcome);
        Assert.Equal(ReviewOutcome.Confirmed, (await DecideAsync(host, tenant, match.Id, confirm: true, key, null)).Outcome);
    }

    [Fact]
    public async Task A_load_recalculates_only_the_touched_inns()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, edo) = await GoldenSetLoader.LoadAsync(host);
        var before = await MatchesAsync(host, tenant);
        Assert.Contains(before, m => m.CounterpartyInn == E);

        // Didox rejects document "100" of E: only E is recalculated, with its whole history.
        host.Time.Advance(TimeSpan.FromDays(1));
        host.FakeEdo.SetStatus(edo, "d15e", 4, host.Time.Now);
        Assert.Equal(SyncRunResult.Succeeded, (await host.SyncEdoAsync(tenant, edo)).Result);

        var run = await host.QueryAsync(tenant, db => db.ReconciliationRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstAsync());
        Assert.Equal(ReconciliationScopeKind.Inns, run.Scope);
        Assert.Equal([E], run.ScopeInns!);
        var after = await MatchesAsync(host, tenant);
        Assert.DoesNotContain(after, m => m.CounterpartyInn == E);
        // Every other match is the same row, untouched by the run.
        Assert.Equal(before.Where(m => m.CounterpartyInn != E).Select(m => (m.Id, m.RunId, m.UpdatedAt)), after.Select(m => (m.Id, m.RunId, m.UpdatedAt)));

        // A load that changes nothing recalculates nothing (and records no run).
        var runs = await host.CountAsync(tenant, db => db.ReconciliationRuns);
        Assert.Equal(SyncRunResult.Succeeded, (await host.SyncEdoAsync(tenant, edo)).Result);
        Assert.Equal(runs, await host.CountAsync(tenant, db => db.ReconciliationRuns));
    }

    [Fact]
    public async Task Incremental_recalculation_of_one_inn_leaves_the_others_alone()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ReconciliationService>().RebuildAsync(CancellationToken.None));
        var before = await MatchesAsync(host, tenant);
        var r = await ReconcileAsync(host, tenant, ReconciliationScope.ForInns([A]));
        Assert.Equal(1, r.Inns);
        Assert.Equal(before.Count(m => m.CounterpartyInn == A), r.Kept);
        Assert.Equal(before.Select(m => (m.Id, m.RunId)), (await MatchesAsync(host, tenant)).Select(m => (m.Id, m.RunId)));
    }

    [Fact]
    public async Task The_database_refuses_a_second_proposed_match_for_the_same_payment()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, _, _) = await GoldenSetLoader.LoadAsync(host);
        var item = await host.QueryAsync(tenant, db => db.ReconciliationMatchItems.AsNoTracking().FirstAsync(i => i.Kind == MatchItemKind.BankTransaction));
        var match = await host.QueryAsync(tenant, db => db.ReconciliationMatches.AsNoTracking().SingleAsync(m => m.Id == item.MatchId));
        var error = await Record.ExceptionAsync(() => host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                var copy = new ReconciliationMatch
                {
                    RunId = match.RunId, MatchType = match.MatchType, Status = MatchStatus.Proposed, CounterpartyInn = match.CounterpartyInn,
                    Currency = "UZS", Signature = "x", CreatedAt = host.Time.Now, UpdatedAt = host.Time.Now,
                };
                db.ReconciliationMatches.Add(copy);
                db.ReconciliationMatchItems.Add(new ReconciliationMatchItem
                    { MatchId = copy.Id, Kind = item.Kind, RefId = item.RefId, AllocatedAmountMinor = 1, MatchStatus = MatchStatus.Proposed });
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return true;
        }));
        Assert.IsType<DbUpdateException>(error);
    }

    [Fact]
    public async Task Too_many_name_spellings_log_one_warning_a_day()
    {
        await using var host = await TestHost.CreateSqliteAsync(s => s.Configure<SearchOptions>(o => o.AliasWarningThreshold = 3));
        var tenant = await host.CreateTenantAsync("Aliases");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var s = host.FakeBank.For(bank);
        s.CounterpartyPoolSize = 50;
        s.MinOperationsPerDay = 5;
        await host.SyncBankAsync(tenant, bank);
        await host.SyncBankAsync(tenant, bank, TestData.Day1.AddDays(-5), TestData.Day1.AddDays(-1));
        string[] Warnings() => host.Logs.Lines.Where(l => l.Contains("Warning") && l.Contains("Search:AliasWarningThreshold")).ToArray();
        Assert.Single(Warnings());
        host.Time.Advance(TimeSpan.FromDays(1.1));
        await host.SyncBankAsync(tenant, bank);
        Assert.Equal(2, Warnings().Length);
    }
}

/// <summary>Reconciliation tables under row-level security, and the golden set on PostgreSQL.</summary>
public class ReconciliationPostgresTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg = new();
    private Guid _tenant;
    private Guid _bank;
    private Guid _other;

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;
        (_tenant, _bank, _) = await GoldenSetLoader.LoadAsync(_pg.Host);
        _other = await _pg.Host.CreateTenantAsync("Other tenant");
    }

    public ValueTask DisposeAsync() => _pg.DisposeAsync();

    [Fact]
    public async Task Golden_set_on_postgres_and_tenant_isolation()
    {
        Assert.SkipUnless(_pg.Available, PostgresFixture.SkipReason);
        var eval = await GoldenSetLoader.EvaluateAsync(_pg.Host, _tenant, _bank);
        Assert.True(eval.Errors.Count == 0, string.Join("\n", eval.Errors));
        Assert.Equal((1.0, 1.0), (eval.Precision, eval.Recall));

        // Another tenant sees nothing, through EF and through the reconciliation queries.
        Assert.Equal(0, await _pg.Host.CountAsync(_other, db => db.ReconciliationMatches));
        Assert.Equal(0, await _pg.Host.CountAsync(_other, db => db.ReconciliationFindings));
        var page = await _pg.Host.InTenantAsync(_other, sp => sp.GetRequiredService<ReconciliationQueries>()
            .GetPaidDocumentsAsync(new DocumentFilter(new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))), null, CancellationToken.None));
        Assert.Empty(page.Data.Items);

        // A decision, so that the decision history has a row of this tenant (also exercises the reversal on PostgreSQL).
        var decided = await _pg.Host.QueryAsync(_tenant, db => db.ReconciliationMatches.Where(m => m.Status == MatchStatus.Proposed)
            .OrderBy(m => m.CounterpartyInn).Select(m => m.Id).FirstAsync());
        await _pg.Host.InTenantAsync(_tenant, async sp =>
        {
            var review = sp.GetRequiredService<ReconciliationReviewService>();
            Assert.Equal(ReviewOutcome.Rejected, (await review.RejectAsync(decided, null, "pg", CancellationToken.None)).Outcome);
            Assert.Equal(ReviewOutcome.Confirmed, (await review.ConfirmAsync(decided, null, "pg", CancellationToken.None)).Outcome);
            return true;
        });

        // The application role without app.tenant_id sees no rows at all (RLS), with it only its own.
        foreach (var table in new[] { "reconciliation_runs", "reconciliation_matches", "reconciliation_match_items", "reconciliation_findings", "reconciliation_match_decisions" })
        {
            Assert.Equal(0L, await AppCountAsync(null, table));
            Assert.True(await AppCountAsync(_tenant, table) > 0, table);
            Assert.Equal(0L, await AppCountAsync(_other, table));
        }
    }

    [Fact]
    public async Task Rls_forbids_writing_reconciliation_rows_of_another_tenant()
    {
        Assert.SkipUnless(_pg.Available, PostgresFixture.SkipReason);
        await using var connection = new NpgsqlConnection(_pg.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{_other}', true)", connection, tx)) await set.ExecuteNonQueryAsync();
        await using var update = new NpgsqlCommand("UPDATE reconciliation_matches SET status = 'Confirmed'", connection, tx);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO reconciliation_runs (id, tenant_id, started_at, scope, match_count, created_count, removed_count, rules_version) " +
            $"VALUES (gen_random_uuid(), '{_tenant}', now(), 'Full', 0, 0, 0, 'x')", connection, tx);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState); // new row violates row-level security policy
    }

    private async Task<long> AppCountAsync(Guid? tenant, string table)
    {
        await using var connection = new NpgsqlConnection(_pg.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        if (tenant is { } t)
        {
            await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{t}', true)", connection, tx);
            await set.ExecuteNonQueryAsync();
        }
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection, tx);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}

/// <summary>Reconciliation reads only the database: no dependency on adapters, sync, queue, secrets or Infrastructure.</summary>
public class ReconciliationArchitectureTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Synect.Application.Adapters", "Synect.Application.Sync", "Synect.Application.Queue",
        "Synect.Application.Resilience", "Synect.Application.Secrets", "Synect.Infrastructure", "Synect.Domain.Raw",
    ];

    [Fact]
    public void Reconciliation_types_do_not_reference_adapters_queue_secrets_or_infrastructure()
    {
        var types = typeof(ReconciliationEngine).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Synect.Application.Reconciliation", StringComparison.Ordinal) == true).ToList();
        Assert.Contains(typeof(ReconciliationService), types);
        Assert.Contains(typeof(ReconciliationQueries), types);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var violations = new List<string>();
        foreach (var type in types)
        {
            var referenced = new List<Type>();
            referenced.AddRange(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            referenced.AddRange(type.GetFields(all).Select(f => f.FieldType));
            referenced.AddRange(type.GetProperties(all).Select(p => p.PropertyType));
            foreach (var method in type.GetMethods(all))
            {
                referenced.Add(method.ReturnType);
                referenced.AddRange(method.GetParameters().Select(p => p.ParameterType));
                referenced.AddRange(method.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []);
            }
            foreach (var t in referenced.SelectMany(Expand))
                if (ForbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
                    violations.Add($"{type.FullName} -> {t.FullName}");
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<Type> Expand(Type t)
    {
        yield return t;
        if (t.HasElementType) foreach (var e in Expand(t.GetElementType()!)) yield return e;
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments().SelectMany(Expand)) yield return g;
    }
}
