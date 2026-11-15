using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Analytics;
using Synect.Application.Reconciliation;
using Synect.Domain.Attention;
using Synect.Domain.Sync;

namespace Synect.Application.Accounts;

/// <summary>One source of an organization as the portfolio tools show it (status, last load, manual onboarding mark).</summary>
public sealed record OrganizationConnection(
    Guid ConnectionId, string Name, string SourceCode, AdapterKind Kind, ConnectionStatus Status, string? StatusReason,
    DateTimeOffset? StatusChangedAt, DateTimeOffset? LastSuccessfulSyncAt, bool? LastLoadComplete,
    OnboardingStatus OnboardingStatus, string? OnboardingNote)
{
    /// <summary>Not Active, never loaded, or flagged by the onboarding mark.</summary>
    public bool NeedsAttention => Status != ConnectionStatus.Active || LastSuccessfulSyncAt is null
                                  || OnboardingStatus == OnboardingStatus.NeedsAttention;

    /// <summary>The source's own staleness threshold (1C: Ingest:AccountingStaleAfter); null for sources that use the reader's threshold.</summary>
    public TimeSpan? StaleAfter { get; init; }

    /// <summary>1C only: freshness of the accounting and the catalog data separately (ingest_freshness).</summary>
    public IReadOnlyList<Ingest.IngestGroupFreshness>? DataGroups { get; init; }
}

/// <param name="NewBySeverity">New attention items (including snoozed ones whose time is over) per severity.</param>
/// <param name="OpenCritical">New and seen critical items.</param>
/// <param name="UnmatchedPayments">Error-free payments in the reconciliation currency with an INN, of the last
/// Accounts:UnmatchedLookbackDays days, in no proposed or confirmed match; null when not requested.</param>
/// <param name="AwaitingOurSignature">Incoming Didox documents in status 2 (waiting for our signature); null when not requested.</param>
public sealed record OrganizationSummary(
    IReadOnlyList<OrganizationConnection> Connections,
    IReadOnlyDictionary<AttentionSeverity, int> NewBySeverity,
    int OpenCritical,
    int? UnmatchedPayments,
    int? AwaitingOurSignature,
    int UnmatchedLookbackDays);

/// <summary>
/// Per-organization figures of the portfolio tools, read in the CURRENT tenant context only (call it once per organization,
/// each in its own scope, through <see cref="OrganizationFanOut"/>). Everything comes from tables that loads,
/// reconciliation and attention rules already maintain (connections, attention_items, reconciliation_match_items,
/// edo_documents); nothing is recalculated on the fly. One transaction with the analytics timeout per organization.
/// </summary>
public sealed class PortfolioQueries(AnalyticsContext ctx, IOptions<AccountsOptions> options, IOptions<ReconciliationOptions> reconciliation, TimeProvider time,
    IOptions<Ingest.IngestOptions> ingest)
{
    public Task<AnalyticsResult<OrganizationSummary>> SummaryAsync(bool withWorkload, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var t = ctx.TenantId;
            var now = time.GetUtcNow();
            var connections = await ctx.Db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(token);
            var groups = connections.Any(c => c.AdapterKind == AdapterKind.Accounting1C)
                ? await ctx.Db.IngestFreshness.AsNoTracking().ToListAsync(token)
                : [];
            var complete = await ctx.QueryAsync<CompleteRow>(new Sql()
                .Raw("select distinct on (r.connection_id) r.connection_id, r.is_complete from sync_runs r where r.tenant_id = ").P(t)
                .Raw(" and r.result = 'Succeeded' order by r.connection_id, r.finished_at desc"), token);
            var completeBy = complete.ToDictionary(r => r.ConnectionId, r => r.IsComplete);

            var attention = await ctx.QueryAsync<AttentionRow>(new Sql()
                .Raw("select i.severity, (count(*) filter (where i.status = 'New' or (i.status = 'Snoozed' and i.snoozed_until <= ").P(now).Raw(")))::int as new_count,")
                .Raw(" (count(*) filter (where i.status in ('New', 'Seen') or (i.status = 'Snoozed' and i.snoozed_until <= ").P(now).Raw(")))::int as open_count")
                .Raw(" from attention_items i where i.tenant_id = ").P(t).Raw(" and i.status in ('New', 'Seen', 'Snoozed') group by i.severity"), token);
            var newBy = Enum.GetValues<AttentionSeverity>().ToDictionary(s => s,
                s => attention.Where(a => a.Severity == s.ToString()).Sum(a => a.NewCount));
            var openCritical = attention.Where(a => a.Severity == nameof(AttentionSeverity.Critical)).Sum(a => a.OpenCount);

            int? unmatched = null, awaiting = null;
            var days = Math.Max(1, options.Value.UnmatchedLookbackDays);
            if (withWorkload)
            {
                var since = ctx.Today.AddDays(-days + 1);
                var counts = await ctx.QueryAsync<WorkloadRow>(new Sql()
                    .Raw("select (select count(*) from bank_transactions b where b.tenant_id = ").P(t)
                    .Raw(" and b.has_error = false and b.currency = ").P(reconciliation.Value.Currency)
                    .Raw(" and coalesce(b.counterparty_inn, '') <> '' and b.posting_date >= ").P(since)
                    .Raw(" and not exists (select 1 from reconciliation_match_items i where i.tenant_id = ").P(t)
                    .Raw(" and i.kind = 'BankTransaction' and i.ref_id = b.id and i.match_status <> 'Rejected'))::int as unmatched,")
                    .Raw(" (select count(*) from edo_documents d where d.tenant_id = ").P(t)
                    .Raw(" and d.owner = 'Incoming' and d.status = ").P(DocumentsAnalytics.AwaitingOurSignature).Raw(")::int as awaiting"), token);
                unmatched = counts[0].Unmatched;
                awaiting = counts[0].Awaiting;
            }

            var list = connections.Select(c => new OrganizationConnection(c.Id, c.Name, c.SourceCode, c.AdapterKind, c.Status, c.StatusReason,
                c.StatusChangedAt, c.LastSuccessfulSyncAt, completeBy.TryGetValue(c.Id, out var done) ? done : null,
                c.OnboardingStatus, c.OnboardingNote)
            {
                StaleAfter = c.AdapterKind == AdapterKind.Accounting1C ? ingest.Value.AccountingStaleAfterOrDefault : null,
                DataGroups = c.AdapterKind == AdapterKind.Accounting1C
                    ? Ingest.IngestFreshnessGroups.Evaluate(groups.Where(g => g.ConnectionId == c.Id).ToDictionary(g => g.DataGroup, g => g.LastAcceptedAt),
                        ingest.Value, now)
                    : null,
            }).ToList();
            return new OrganizationSummary(list, newBy, openCritical, unmatched, awaiting, days);
        }, ct);

    private sealed class CompleteRow
    {
        public Guid ConnectionId { get; set; }
        public bool IsComplete { get; set; }
    }

    private sealed class AttentionRow
    {
        public string Severity { get; set; } = "";
        public int NewCount { get; set; }
        public int OpenCount { get; set; }
    }

    private sealed class WorkloadRow
    {
        public int Unmatched { get; set; }
        public int Awaiting { get; set; }
    }
}
