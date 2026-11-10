using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Attention;
using Synect.Application.Analytics;
using Synect.Domain.Attention;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;
using static Synect.Application.Attention.AttentionRuleCodes;

namespace Synect.Tests.Attention;

/// <summary>Loads the attention golden set into a new tenant of a host (fake adapters, real sync services, fixed clock).</summary>
internal static class AttentionKit
{
    public static async Task<(Guid Tenant, AttentionGoldenSet.Connections Connections)> LoadGoldenAsync(TestHost host, string name)
    {
        var tenant = await host.CreateTenantAsync(name);
        var c = new AttentionGoldenSet.Connections(
            await host.CreateConnectionAsync(tenant, AdapterKind.Bank), await host.CreateConnectionAsync(tenant, AdapterKind.Edo),
            await host.CreateConnectionAsync(tenant, AdapterKind.Bank), await host.CreateConnectionAsync(tenant, AdapterKind.Edo));
        AttentionGoldenSet.Apply(host.FakeBank, host.FakeEdo, c);
        await AttentionGoldenSet.LoadAsync(c, host.Time.Set,
            (id, from, to) => host.SyncBankAsync(tenant, id, from, to), id => host.SyncEdoAsync(tenant, id));
        return (tenant, c);
    }

    public static Task<AttentionEvaluation> EvaluateAsync(this TestHost host, Guid tenant, params AttentionCategory[] categories) =>
        host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionEvaluator>()
            .EvaluateAsync(categories.Length == 0 ? AttentionEvaluator.AllCategories : categories, CancellationToken.None));

    public static Task<List<AttentionItem>> ItemsAsync(this TestHost host, Guid tenant) =>
        host.QueryAsync(tenant, db => db.AttentionItems.AsNoTracking().OrderBy(i => i.RuleCode).ThenBy(i => i.DedupKey).ToListAsync());

    public static async Task<AttentionItem> ItemAsync(this TestHost host, Guid tenant, string rule) =>
        Assert.Single(await host.ItemsAsync(tenant), i => i.RuleCode == rule);

    public static Task<List<AttentionEventKind>> EventsAsync(this TestHost host, Guid tenant, Guid item) =>
        host.QueryAsync(tenant, db => db.AttentionItemEvents.AsNoTracking().Where(e => e.ItemId == item).OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => e.Kind).ToListAsync());

    public static Task<AttentionUpdateResult> UpdateAsync(this TestHost host, Guid tenant, Guid item, AttentionAction action,
        DateTimeOffset? until = null, string? comment = null) =>
        host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionService>().UpdateAsync(item, action, until, comment, null, CancellationToken.None));

    public static Task<AnalyticsResult<Page<AttentionItemView>>> ListAsync(this TestHost host, Guid tenant, AttentionFilter? filter = null) =>
        host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionService>().ListAsync(filter ?? new AttentionFilter(), null, CancellationToken.None));

    /// <summary>Cases in an item: the count of a grouped item, 1 for a single case.</summary>
    public static int Cases(AttentionItem i)
    {
        using var doc = JsonDocument.Parse(i.Details);
        return i.EntityKind == "tenant" ? doc.RootElement.GetProperty("count").GetInt32() : 1;
    }

    /// <summary>Adds Didox documents to a connection's fake source (they are read by the next incremental load).</summary>
    public static void AddDocuments(TestHost host, Guid edo, params FakeDidoxDocument[] documents) =>
        host.FakeEdo.For(edo).UseDocuments(host.FakeEdo.Documents(edo).Concat(documents));

    public static FakeDidoxDocument Waiting(string id, DateOnly date, DateTimeOffset updated) => new()
    {
        DocId = id, DocType = "002", Owner = 0, Status = 2, Name = id.ToUpperInvariant(), DocDate = date, PartnerTin = "309000099",
        PartnerCompany = "PARTNER", TotalSum = 1_000m, TotalVatSum = 107.14m, Created = updated, Updated = updated,
    };
}

/// <summary>Attention rules and the life cycle of attention items on PostgreSQL (the rules use PostgreSQL SQL).</summary>
public class AttentionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;

    private void RequirePostgres() => Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);

    [Fact]
    public async Task Golden_set_creates_every_expected_item_with_its_severity_and_nothing_else()
    {
        RequirePostgres();
        var (tenant, _) = await AttentionKit.LoadGoldenAsync(Host, "Attention rules");
        // The loads already evaluated the rules (after every load); a full evaluation finds nothing new.
        var run = await Host.EvaluateAsync(tenant);
        Assert.Equal(0, run.Created);

        var items = (await Host.ItemsAsync(tenant)).Where(i => i.Status != AttentionStatus.Resolved).ToList();
        foreach (var e in AttentionGoldenSet.Expected)
        {
            var item = Assert.Single(items, i => i.RuleCode == e.RuleCode);
            Assert.Equal(e.Severity, item.Severity);
            Assert.Equal(AttentionStatus.New, item.Status);
            Assert.Equal(e.Cases, AttentionKit.Cases(item));
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Body));
        }
        Assert.Equal(AttentionGoldenSet.Expected.Count, items.Count);
        foreach (var n in AttentionGoldenSet.ExpectedNotCreated)
            Assert.DoesNotContain(items, i => i.RuleCode == n.RuleCode);
        Assert.Equal(0, run.Findings[ConnectionIncompleteLoad]);
        Assert.Equal(0, run.Findings[SignedUnpaidActs]);

        // Details: identifiers and amounts; accounts masked; the bank amount unit note where bank amounts are used.
        var large = items.Single(i => i.RuleCode == LargeUnmatchedPayments);
        using var details = JsonDocument.Parse(large.Details);
        var entry = details.RootElement.GetProperty("items")[0];
        Assert.Equal(AttentionGoldenSet.LargePaymentAmount, entry.GetProperty("amount_minor").GetInt64());
        Assert.Equal(AttentionGoldenSet.LargePaymentInn, entry.GetProperty("inn").GetString());
        Assert.StartsWith("****", entry.GetProperty("account").GetString());
        Assert.Contains("not confirmed", details.RootElement.GetProperty("note").GetString());
        var mismatch = items.Single(i => i.RuleCode == PaymentAccountMismatch);
        Assert.DoesNotContain(ReconciliationGoldenSet.DOtherAccount, mismatch.Details + mismatch.Title);
        Assert.Contains("****0099", mismatch.Title);
        // A single case is keyed by its significant parameters (here the amount).
        var duplicate = items.Single(i => i.RuleCode == PossibleDuplicatePayment);
        Assert.EndsWith(":25000000", duplicate.DedupKey);
        // Every item has a Created event.
        foreach (var i in items) Assert.Equal([AttentionEventKind.Created], await Host.EventsAsync(tenant, i.Id));
    }

    [Fact]
    public async Task Incomplete_bank_load_and_old_signed_unpaid_act_create_items()
    {
        RequirePostgres();
        var (tenant, c) = await AttentionKit.LoadGoldenAsync(Host, "Attention incomplete");
        // A day older than yesterday that the bank has not closed.
        Host.FakeBank.For(c.Bank).OpenDays.Add(new DateOnly(2026, 10, 30));
        Assert.Equal(SyncRunResult.Succeeded, (await Host.SyncBankAsync(tenant, c.Bank, new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 30))).Result);
        var incomplete = await Host.ItemAsync(tenant, ConnectionIncompleteLoad);
        Assert.Equal(AttentionSeverity.High, incomplete.Severity);
        Assert.Equal(c.Bank.ToString(), incomplete.EntityRef);

        // АКТ-5 (2026-10-20, signed, unpaid) becomes older than 30 days.
        Host.Time.Set(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(5)));
        await Host.EvaluateAsync(tenant, AttentionCategory.Reconciliation);
        var acts = await Host.ItemAsync(tenant, SignedUnpaidActs);
        Assert.Equal(AttentionSeverity.Low, acts.Severity);
        Assert.Equal(1, AttentionKit.Cases(acts));
        Assert.Contains("АКТ-5", acts.Details);
    }

    [Fact]
    public async Task Evaluation_is_idempotent_and_never_duplicates_a_case()
    {
        RequirePostgres();
        var (tenant, c) = await AttentionKit.LoadGoldenAsync(Host, "Attention idempotent");
        var before = await Host.ItemsAsync(tenant);
        var events = await Host.CountAsync(tenant, db => db.AttentionItemEvents);

        for (var i = 0; i < 3; i++)
        {
            var r = await Host.EvaluateAsync(tenant);
            Assert.Equal((0, 0, 0), (r.Created, r.Reopened, r.Resolved));
        }
        var after = await Host.ItemsAsync(tenant);
        Assert.Equal(before.Select(i => (i.Id, i.DedupKey, i.Status)), after.Select(i => (i.Id, i.DedupKey, i.Status)));
        Assert.Equal(events, await Host.CountAsync(tenant, db => db.AttentionItemEvents));
        Assert.Equal(after.Count, after.Select(i => i.DedupKey).Distinct().Count());

        // A repeated load of the same data does not create anything either.
        await Host.SyncEdoAsync(tenant, c.Edo);
        await Host.SyncBankAsync(tenant, c.Bank, ReconciliationGoldenSet.BankFrom, ReconciliationGoldenSet.BankTo);
        Assert.Equal(events, await Host.CountAsync(tenant, db => db.AttentionItemEvents));
    }

    [Fact]
    public async Task Item_is_resolved_when_the_condition_is_gone_and_reopened_when_it_comes_back()
    {
        RequirePostgres();
        var (tenant, c) = await AttentionKit.LoadGoldenAsync(Host, "Attention resolve");
        var item = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(AttentionUpdateOutcome.Changed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Seen)).Outcome);

        // w1 gets signed: the condition is gone.
        Host.Time.Advance(TimeSpan.FromHours(1));
        Host.FakeEdo.SetStatus(c.Edo, "w1", 3, Host.Time.Now);
        await Host.SyncEdoAsync(tenant, c.Edo);
        var resolved = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(AttentionStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedAt);

        // Back to "awaiting our signature": the same row opens again as New.
        Host.Time.Advance(TimeSpan.FromHours(1));
        Host.FakeEdo.SetStatus(c.Edo, "w1", 2, Host.Time.Now);
        await Host.SyncEdoAsync(tenant, c.Edo);
        var reopened = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(item.Id, reopened.Id);
        Assert.Equal(AttentionStatus.New, reopened.Status);
        Assert.Null(reopened.ResolvedAt);
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Seen, AttentionEventKind.AutoResolved, AttentionEventKind.Reopened],
            await Host.EventsAsync(tenant, item.Id));

        // A resolved item cannot be changed by a person.
        Host.FakeEdo.SetStatus(c.Edo, "w1", 3, Host.Time.Now.AddMinutes(5));
        Host.Time.Advance(TimeSpan.FromMinutes(10));
        await Host.SyncEdoAsync(tenant, c.Edo);
        var refused = await Host.UpdateAsync(tenant, item.Id, AttentionAction.Dismiss, comment: "not needed");
        Assert.Equal(AttentionUpdateOutcome.NotAllowed, refused.Outcome);
    }

    [Fact]
    public async Task Dismissed_item_stays_dismissed_until_its_significant_content_changes()
    {
        RequirePostgres();
        var (tenant, c) = await AttentionKit.LoadGoldenAsync(Host, "Attention dismiss");
        var item = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(AttentionUpdateOutcome.Changed,
            (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Dismiss, comment: "Signed on paper, Didox is updated later")).Outcome);

        // Same content: stays dismissed on every evaluation.
        await Host.EvaluateAsync(tenant);
        Assert.Equal(AttentionStatus.Dismissed, (await Host.ItemAsync(tenant, AwaitingOurSignature)).Status);

        // The condition goes away and comes back with the same document: still dismissed (no new case).
        Host.Time.Advance(TimeSpan.FromHours(1));
        Host.FakeEdo.SetStatus(c.Edo, "w1", 3, Host.Time.Now);
        await Host.SyncEdoAsync(tenant, c.Edo);
        Assert.Equal(AttentionStatus.Dismissed, (await Host.ItemAsync(tenant, AwaitingOurSignature)).Status);
        Host.Time.Advance(TimeSpan.FromHours(1));
        Host.FakeEdo.SetStatus(c.Edo, "w1", 2, Host.Time.Now);
        await Host.SyncEdoAsync(tenant, c.Edo);
        var still = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(AttentionStatus.Dismissed, still.Status);
        Assert.Equal("Signed on paper, Didox is updated later", still.Comment);

        // A new document waiting for our signature is a significant change: the item opens again.
        Host.Time.Advance(TimeSpan.FromHours(1));
        AttentionKit.AddDocuments(Host, c.Edo, AttentionKit.Waiting("w9", new DateOnly(2026, 10, 20), Host.Time.Now));
        await Host.SyncEdoAsync(tenant, c.Edo);
        var reopened = await Host.ItemAsync(tenant, AwaitingOurSignature);
        Assert.Equal(item.Id, reopened.Id);
        Assert.Equal(AttentionStatus.New, reopened.Status);
        Assert.Equal(2, AttentionKit.Cases(reopened));
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Dismissed, AttentionEventKind.Reopened], await Host.EventsAsync(tenant, item.Id));

        // Dismiss needs a comment.
        await Assert.ThrowsAsync<ArgumentException>(() => Host.UpdateAsync(tenant, item.Id, AttentionAction.Dismiss));
    }

    [Fact]
    public async Task Snoozed_item_comes_back_as_new_after_its_time_if_the_condition_holds()
    {
        RequirePostgres();
        var (tenant, _) = await AttentionKit.LoadGoldenAsync(Host, "Attention snooze");
        var item = await Host.ItemAsync(tenant, ConnectionStaleData);
        var until = Host.Time.Now.AddDays(1);
        var r = await Host.UpdateAsync(tenant, item.Id, AttentionAction.Snooze, until);
        Assert.Equal((AttentionUpdateOutcome.Changed, AttentionStatus.Snoozed, until), (r.Outcome, r.Status!.Value, r.SnoozedUntil!.Value));

        // Hidden from the default list (new and seen), visible with status = snoozed.
        Assert.DoesNotContain((await Host.ListAsync(tenant)).Data.Items, i => i.Id == item.Id);
        Assert.Contains((await Host.ListAsync(tenant, new AttentionFilter(Statuses: [AttentionStatus.Snoozed]))).Data.Items, i => i.Id == item.Id);
        await Host.EvaluateAsync(tenant);
        Assert.Equal(AttentionStatus.Snoozed, (await Host.ItemAsync(tenant, ConnectionStaleData)).Status);

        // After the snooze: New in lists at once, stored as New (event Reopened) by the next evaluation.
        Host.Time.Advance(TimeSpan.FromHours(25));
        var listed = Assert.Single((await Host.ListAsync(tenant)).Data.Items, i => i.Id == item.Id);
        Assert.Equal(AttentionStatus.New, listed.Status);
        await Host.EvaluateAsync(tenant, AttentionCategory.Connection);
        var back = await Host.ItemAsync(tenant, ConnectionStaleData);
        Assert.Equal((AttentionStatus.New, (DateTimeOffset?)null), (back.Status, back.SnoozedUntil));
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Snoozed, AttentionEventKind.Reopened], await Host.EventsAsync(tenant, item.Id));

        // A snooze longer than Attention:MaxSnoozeDays or in the past is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => Host.UpdateAsync(tenant, item.Id, AttentionAction.Snooze, Host.Time.Now.AddDays(91)));
        await Assert.ThrowsAsync<ArgumentException>(() => Host.UpdateAsync(tenant, item.Id, AttentionAction.Snooze, Host.Time.Now.AddMinutes(-1)));
    }

    [Fact]
    public async Task Many_cases_become_one_grouped_item_with_a_count_and_a_capped_list()
    {
        RequirePostgres();
        await using var host = TestHost.CreatePostgres(fixture.AppConnectionString, () => Task.CompletedTask, s => s.Configure<AttentionOptions>(o =>
        {
            o.GroupAbove = 0;              // single-case rules group from the first case
            o.MaxListedEntries = 1;
            o.AwaitingOurSignatureDays = 0; // w1 (11 days) and w2 (2 days)
        }));
        var (tenant, _) = await AttentionKit.LoadGoldenAsync(host, "Attention grouping");
        var items = (await host.ItemsAsync(tenant)).Where(i => i.Status != AttentionStatus.Resolved).ToList();

        var duplicates = Assert.Single(items, i => i.RuleCode == PossibleDuplicatePayment);
        Assert.Equal((PossibleDuplicatePayment + ":all", "tenant"), (duplicates.DedupKey, duplicates.EntityKind));
        var waiting = Assert.Single(items, i => i.RuleCode == AwaitingOurSignature);
        using var doc = JsonDocument.Parse(waiting.Details);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("listed").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, waiting.MemberKeys.Length);
        var total = Assert.Single(doc.RootElement.GetProperty("totals").EnumerateArray());
        Assert.Equal(2, total.GetProperty("count").GetInt32());
        Assert.Equal(1_500_000 + 1_600_000, total.GetProperty("amount_minor").GetInt64());
        Assert.StartsWith("2 incoming documents", waiting.Title);
    }

    [Fact]
    public async Task Clients_never_see_or_change_each_others_items()
    {
        RequirePostgres();
        var (a, _) = await AttentionKit.LoadGoldenAsync(Host, "Attention A");
        var b = await Host.CreateTenantAsync("Attention B");
        var itemOfA = (await Host.ItemsAsync(a)).First();

        Assert.Empty(await Host.ItemsAsync(b));
        Assert.Empty((await Host.ListAsync(b, new AttentionFilter(Statuses: Enum.GetValues<AttentionStatus>()))).Data.Items);
        var summary = await Host.InTenantAsync(b, sp => sp.GetRequiredService<AttentionService>().SummaryAsync(CancellationToken.None));
        Assert.Equal((0, 0), (summary.Data.Open, summary.Data.New));
        Assert.Equal(AttentionUpdateOutcome.NotFound, (await Host.UpdateAsync(b, itemOfA.Id, AttentionAction.Seen)).Outcome);
        // Row-level security: without a tenant nothing is visible at all.
        Assert.Equal(0, await Host.CountAsync(null, db => db.AttentionItems.IgnoreQueryFilters()));
        Assert.Equal(0, await Host.CountAsync(null, db => db.AttentionItemEvents.IgnoreQueryFilters()));
        Assert.Equal(0, await Host.CountAsync(b, db => db.AttentionItems.IgnoreQueryFilters()));
        Assert.True(await fixture.ScalarAsync<long>($"SELECT count(*) FROM attention_items WHERE tenant_id = '{a}'") > 0);
    }

    [Fact]
    public async Task Person_actions_follow_the_allowed_transitions_and_are_recorded()
    {
        RequirePostgres();
        var (tenant, _) = await AttentionKit.LoadGoldenAsync(Host, "Attention actions");
        var item = await Host.ItemAsync(tenant, PaymentAccountMismatch);

        Assert.Equal(AttentionUpdateOutcome.Unchanged, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Reopen)).Outcome);
        Assert.Equal(AttentionUpdateOutcome.Changed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Seen)).Outcome);
        Assert.Equal(AttentionUpdateOutcome.Unchanged, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Seen)).Outcome);
        Assert.Equal(AttentionUpdateOutcome.Changed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Dismiss, comment: "Second account of the supplier")).Outcome);
        Assert.Equal(AttentionUpdateOutcome.NotAllowed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Seen)).Outcome);
        Assert.Equal(AttentionUpdateOutcome.NotAllowed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Snooze, Host.Time.Now.AddDays(1))).Outcome);
        Assert.Equal(AttentionUpdateOutcome.Changed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Reopen)).Outcome);
        Assert.Equal(AttentionUpdateOutcome.NotFound, (await Host.UpdateAsync(tenant, Guid.NewGuid(), AttentionAction.Seen)).Outcome);
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Seen, AttentionEventKind.Dismissed, AttentionEventKind.Reopened],
            await Host.EventsAsync(tenant, item.Id));

        // The history is append-only.
        await Host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var e = await db.InTransactionAsync(t => db.AttentionItemEvents.FirstAsync(x => x.ItemId == item.Id, t), CancellationToken.None);
            e.Comment = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.InTransactionAsync(t => db.SaveChangesAsync(t), CancellationToken.None));
            return true;
        });
    }

    [Fact]
    public async Task List_is_sorted_by_severity_filtered_and_paged_with_a_cursor()
    {
        RequirePostgres();
        var (tenant, _) = await AttentionKit.LoadGoldenAsync(Host, "Attention list");
        var all = (await Host.ListAsync(tenant)).Data.Items;
        Assert.Equal(AttentionGoldenSet.Expected.Count, all.Count);
        Assert.Equal(all.Select(i => (int)i.Severity).Order(), all.Select(i => (int)i.Severity));
        Assert.Equal(AttentionSeverity.Critical, all[0].Severity);

        var high = (await Host.ListAsync(tenant, new AttentionFilter(Severities: [AttentionSeverity.High]))).Data.Items;
        Assert.Equal(AttentionGoldenSet.Expected.Count(e => e.Severity == AttentionSeverity.High), high.Count);
        var docs = (await Host.ListAsync(tenant, new AttentionFilter(Categories: [AttentionCategory.Documents]))).Data.Items;
        Assert.All(docs, i => Assert.Equal(AttentionCategory.Documents, i.Category));
        Assert.Equal(3, docs.Count);

        var paged = new List<AttentionItemView>();
        string? cursor = null;
        do
        {
            var page = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionService>()
                .ListAsync(new AttentionFilter(), new PageRequest(4, cursor), CancellationToken.None));
            paged.AddRange(page.Data.Items);
            cursor = page.Data.NextCursor;
        } while (cursor is not null);
        Assert.Equal(all.Select(i => i.Id), paged.Select(i => i.Id));

        var counts = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionService>().CountsAsync(CancellationToken.None));
        Assert.Equal((AttentionGoldenSet.Expected.Count, 1, 4, 1), (counts.New, counts.NewCritical, counts.NewHigh, counts.OpenCritical));
    }

    [Fact]
    public async Task Periodic_evaluation_runs_at_most_once_per_interval_and_a_failed_load_reevaluates_connections()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Attention periodic");
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var first = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionTrigger>().PeriodicAsync(CancellationToken.None));
        Assert.NotNull(first);
        Assert.Null(await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionTrigger>().PeriodicAsync(CancellationToken.None)));
        Host.Time.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionTrigger>().PeriodicAsync(CancellationToken.None)));

        // An authentication failure opens the circuit; the failed load evaluates the connection rules at once.
        Host.FakeBank.For(bank).AuthFailure = true;
        Assert.Equal(SyncRunResult.Failed, (await Host.SyncBankAsync(tenant, bank)).Result);
        var item = await Host.ItemAsync(tenant, ConnectionNeedsAttention);
        Assert.Equal(AttentionSeverity.Critical, item.Severity);
        Assert.DoesNotContain(Host.Logs.Lines, l => Host.Passwords.Values.Any(p => l.Contains(p)));
    }
}

/// <summary>The attention rules read only the database: no adapters, sync, resilience, secrets, raw data or Infrastructure.</summary>
public class AttentionArchitectureTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Synect.Application.Adapters", "Synect.Application.Sync", "Synect.Application.Resilience", "Synect.Application.Secrets",
        "Synect.Infrastructure", "Synect.Domain.Raw",
    ];

    [Fact]
    public void Attention_types_do_not_reference_adapters_secrets_or_infrastructure()
    {
        var types = typeof(AttentionEvaluator).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Synect.Application.Attention", StringComparison.Ordinal) == true).ToList();
        Assert.Contains(typeof(AttentionService), types);

        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
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
            foreach (var t in referenced)
                if (ForbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
                    violations.Add($"{type.FullName} -> {t.FullName}");
        }
        Assert.Empty(violations);
    }
}
