using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Domain.Attention;

namespace Synect.Application.Attention;

/// <param name="Status">Effective status: a Snoozed item whose time is over is New.</param>
/// <param name="Details">JSON object (identifiers and amounts).</param>
public sealed record AttentionItemView(
    Guid Id, string RuleCode, AttentionCategory Category, AttentionSeverity Severity, AttentionStatus Status,
    string Title, string Body, string Details, string EntityKind, string EntityRef,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? SnoozedUntil, DateTimeOffset? ResolvedAt,
    Guid? DecidedByKey, string? Comment);

/// <summary>Empty or null lists mean "any"; Statuses null means New and Seen.</summary>
public sealed record AttentionFilter(
    IReadOnlyCollection<AttentionSeverity>? Severities = null,
    IReadOnlyCollection<AttentionCategory>? Categories = null,
    IReadOnlyCollection<AttentionStatus>? Statuses = null)
{
    public static readonly IReadOnlyList<AttentionStatus> DefaultStatuses = [AttentionStatus.New, AttentionStatus.Seen];
}

public sealed record AttentionCount(AttentionSeverity Severity, AttentionCategory Category, int Open, int New);

/// <param name="Open">New and Seen items (the inbox).</param>
public sealed record AttentionSummary(
    int Open, int New, IReadOnlyDictionary<AttentionSeverity, int> OpenBySeverity, IReadOnlyDictionary<AttentionSeverity, int> NewBySeverity,
    IReadOnlyDictionary<AttentionCategory, int> OpenByCategory, IReadOnlyList<AttentionCount> Breakdown, int Snoozed, int Dismissed,
    DateTimeOffset? LastEvaluatedAt);

/// <summary>The short block added to other tool answers.</summary>
public sealed record AttentionCounts(int New, int NewCritical, int NewHigh, int OpenCritical);

public enum AttentionAction
{
    Seen,
    Snooze,
    Dismiss,
    Reopen,
}

public enum AttentionUpdateOutcome
{
    Changed,

    /// <summary>The item already was in the requested state (no event written).</summary>
    Unchanged,

    /// <summary>The action does not apply to the item's state (e.g. dismiss a resolved item); nothing changed.</summary>
    NotAllowed,

    NotFound,
}

public sealed record AttentionUpdateResult(
    AttentionUpdateOutcome Outcome, Guid ItemId, AttentionStatus? PreviousStatus, AttentionStatus? Status,
    DateTimeOffset? SnoozedUntil = null, Guid? EventId = null, string? Reason = null);

/// <summary>
/// Reads the attention inbox of the current tenant (RLS) and applies the accountant's decisions (seen, snooze, dismiss,
/// reopen). Decisions change only Synect's attention_items and are appended to attention_item_events.
/// </summary>
public sealed class AttentionService(AnalyticsContext ctx, ISynectDbContext db, ITenantContext tenant, IOptions<AttentionOptions> options, TimeProvider time)
{
    /// <summary>Items ordered by severity (Critical first), then the newest case first. Keyset cursor.</summary>
    public Task<AnalyticsResult<Page<AttentionItemView>>> ListAsync(AttentionFilter filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var now = time.GetUtcNow();
            var statuses = (filter.Statuses is { Count: > 0 } s ? s : AttentionFilter.DefaultStatuses).Select(x => x.ToString()).ToArray();
            var sql = new Sql()
                .Raw("select * from (select i.id, i.rule_code, i.category, i.severity, i.title, i.body, i.details::text as details, i.entity_kind,")
                .Raw(" i.entity_ref, i.first_seen_at, i.last_seen_at, i.snoozed_until, i.resolved_at, i.decided_by_key, i.comment,")
                .Raw(" case i.severity when 'Critical' then 1 when 'High' then 2 when 'Medium' then 3 else 4 end as rank,")
                .Raw(" case when i.status = 'Snoozed' and i.snoozed_until <= ").P(now).Raw(" then 'New' else i.status end as status")
                .Raw(" from attention_items i where i.tenant_id = ").P(ctx.TenantId).Raw(") x where x.status = any(").P(statuses).Raw(")");
            if (filter.Severities is { Count: > 0 } sev) sql.Raw(" and x.severity = any(").P(sev.Select(v => v.ToString()).ToArray()).Raw(")");
            if (filter.Categories is { Count: > 0 } cat) sql.Raw(" and x.category = any(").P(cat.Select(v => v.ToString()).ToArray()).Raw(")");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 3);
                var rank = int.Parse(c[0]!, System.Globalization.CultureInfo.InvariantCulture);
                var first = DateTimeOffset.Parse(c[1]!, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
                var id = Guid.Parse(c[2]!);
                sql.Raw(" and (x.rank > ").P(rank).Raw(" or (x.rank = ").P(rank).Raw(" and (x.first_seen_at < ").P(first)
                    .Raw(" or (x.first_seen_at = ").P(first).Raw(" and x.id > ").P(id).Raw("))))");
            }
            sql.Raw(" order by x.rank, x.first_seen_at desc, x.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<ItemRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit, r => new AttentionItemView(r.Id, r.RuleCode, Enum.Parse<AttentionCategory>(r.Category),
                    Enum.Parse<AttentionSeverity>(r.Severity), Enum.Parse<AttentionStatus>(r.Status), r.Title, r.Body, r.Details, r.EntityKind,
                    r.EntityRef, r.FirstSeenAt, r.LastSeenAt, r.Status == nameof(AttentionStatus.New) ? null : r.SnoozedUntil, r.ResolvedAt,
                    r.DecidedByKey, r.Comment),
                r => Cursor.Encode(r.Rank, r.FirstSeenAt, r.Id));
        }, ct);

    /// <summary>Whether the current tenant has an attention item with this id (any status).</summary>
    public Task<bool> ExistsAsync(Guid itemId, CancellationToken ct) =>
        db.InTransactionAsync(t => db.AttentionItems.AsNoTracking().AnyAsync(i => i.Id == itemId, t), ct);

    public Task<AnalyticsResult<AttentionSummary>> SummaryAsync(CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var now = time.GetUtcNow();
            var rows = await OpenRowsAsync(token);
            var effective = rows.Select(r => (r.Severity, r.Category, Status: Effective(r.Status, r.SnoozedUntil, now))).ToList();
            var inbox = effective.Where(r => r.Status is AttentionStatus.New or AttentionStatus.Seen).ToList();
            var fresh = inbox.Where(r => r.Status == AttentionStatus.New).ToList();
            var lastEvaluated = await db.AttentionItems.AsNoTracking().MaxAsync(i => (DateTimeOffset?)i.LastSeenAt, token);
            return new AttentionSummary(
                inbox.Count, fresh.Count,
                Enum.GetValues<AttentionSeverity>().ToDictionary(s => s, s => inbox.Count(r => r.Severity == s)),
                Enum.GetValues<AttentionSeverity>().ToDictionary(s => s, s => fresh.Count(r => r.Severity == s)),
                Enum.GetValues<AttentionCategory>().ToDictionary(c => c, c => inbox.Count(r => r.Category == c)),
                inbox.GroupBy(r => (r.Severity, r.Category)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Category)
                    .Select(g => new AttentionCount(g.Key.Severity, g.Key.Category, g.Count(), g.Count(r => r.Status == AttentionStatus.New))).ToList(),
                effective.Count(r => r.Status == AttentionStatus.Snoozed),
                effective.Count(r => r.Status == AttentionStatus.Dismissed),
                lastEvaluated);
        }, ct);

    /// <summary>Counts for the attention block of other answers (one small query, own transaction or the current one).</summary>
    public Task<AttentionCounts> CountsAsync(CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var now = time.GetUtcNow();
            var rows = (await OpenRowsAsync(token)).Select(r => (r.Severity, Status: Effective(r.Status, r.SnoozedUntil, now))).ToList();
            var fresh = rows.Where(r => r.Status == AttentionStatus.New).ToList();
            return new AttentionCounts(fresh.Count, fresh.Count(r => r.Severity == AttentionSeverity.Critical),
                fresh.Count(r => r.Severity == AttentionSeverity.High),
                rows.Count(r => r.Severity == AttentionSeverity.Critical && r.Status is AttentionStatus.New or AttentionStatus.Seen));
        }, ct);

    /// <summary>
    /// seen: New -> Seen. snooze: New, Seen or Snoozed -> Snoozed until <paramref name="snoozeUntil"/>. dismiss: New, Seen or
    /// Snoozed -> Dismissed (comment required). reopen: Seen, Snoozed or Dismissed -> New. A Resolved item cannot be changed
    /// (its condition is gone; it opens again by itself when the condition comes back); neither can an Archived one.
    /// </summary>
    public Task<AttentionUpdateResult> UpdateAsync(
        Guid itemId, AttentionAction action, DateTimeOffset? snoozeUntil, string? comment, Guid? apiKeyId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Attention requires a tenant context.");
            await AttentionEvaluator.LockTenantAsync(db, tenantId, token);
            var item = await db.AttentionItems.SingleOrDefaultAsync(i => i.Id == itemId, token);
            if (item is null) return new AttentionUpdateResult(AttentionUpdateOutcome.NotFound, itemId, null, null);

            var now = time.GetUtcNow();
            var previous = item.EffectiveStatus(now);
            var text = string.IsNullOrWhiteSpace(comment) ? null
                : comment.Trim() is { Length: > AttentionItem.MaxCommentLength } c ? c[..AttentionItem.MaxCommentLength] : comment.Trim();

            AttentionUpdateResult Refuse(string reason) => new(AttentionUpdateOutcome.NotAllowed, itemId, previous, previous, item.SnoozedUntil, Reason: reason);
            AttentionUpdateResult Same() => new(AttentionUpdateOutcome.Unchanged, itemId, previous, previous, previous == AttentionStatus.Snoozed ? item.SnoozedUntil : null);

            if (previous == AttentionStatus.Resolved)
                return Refuse("The item is resolved: its condition is no longer found. It opens again by itself if the condition comes back.");
            if (previous == AttentionStatus.Archived)
                return Refuse("The item is archived: it was dismissed and its condition has been gone for a long time. It opens again by itself if the condition comes back.");

            AttentionStatus next;
            AttentionEventKind kind;
            DateTimeOffset? until = null;
            switch (action)
            {
                case AttentionAction.Seen:
                    if (previous == AttentionStatus.Seen) return Same();
                    if (previous != AttentionStatus.New) return Refuse($"Only a new item can be marked as seen; this one is {previous.ToString().ToLowerInvariant()} (use reopen first).");
                    (next, kind) = (AttentionStatus.Seen, AttentionEventKind.Seen);
                    break;
                case AttentionAction.Snooze:
                    if (previous == AttentionStatus.Dismissed) return Refuse("The item is dismissed; reopen it first to snooze it.");
                    if (snoozeUntil is not { } u || u <= now) throw new ArgumentException("The snooze must end in the future.");
                    if (u > now.AddDays(options.Value.MaxSnoozeDays)) throw new ArgumentException($"The snooze can last at most {options.Value.MaxSnoozeDays} days.");
                    (next, kind, until) = (AttentionStatus.Snoozed, AttentionEventKind.Snoozed, u);
                    break;
                case AttentionAction.Dismiss:
                    if (text is null) throw new ArgumentException("A comment is required to dismiss an item (why it needs nothing).");
                    if (previous == AttentionStatus.Dismissed) return Same();
                    (next, kind) = (AttentionStatus.Dismissed, AttentionEventKind.Dismissed);
                    break;
                case AttentionAction.Reopen:
                    if (previous == AttentionStatus.New) return Same();
                    (next, kind) = (AttentionStatus.New, AttentionEventKind.Reopened);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            item.Status = next;
            item.SnoozedUntil = until;
            item.DecidedByKey = apiKeyId;
            if (text is not null) item.Comment = text;
            item.UpdatedAt = now;
            var history = new AttentionItemEvent { ItemId = item.Id, Kind = kind, At = now, ApiKeyId = apiKeyId, Comment = text };
            db.AttentionItemEvents.Add(history);
            await db.SaveChangesAsync(token);
            return new AttentionUpdateResult(AttentionUpdateOutcome.Changed, itemId, previous, next, until, history.Id);
        }, ct);

    private static AttentionStatus Effective(AttentionStatus status, DateTimeOffset? until, DateTimeOffset now) =>
        status == AttentionStatus.Snoozed && until is { } u && u <= now ? AttentionStatus.New : status;

    private async Task<List<OpenRow>> OpenRowsAsync(CancellationToken ct) =>
        (await db.AttentionItems.AsNoTracking().Where(i => i.Status != AttentionStatus.Resolved && i.Status != AttentionStatus.Archived)
            .Select(i => new { i.Severity, i.Category, i.Status, i.SnoozedUntil }).ToListAsync(ct))
        .Select(r => new OpenRow(r.Severity, r.Category, r.Status, r.SnoozedUntil)).ToList();

    private sealed record OpenRow(AttentionSeverity Severity, AttentionCategory Category, AttentionStatus Status, DateTimeOffset? SnoozedUntil);

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public string RuleCode { get; set; } = "";
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Details { get; set; } = "{}";
        public string EntityKind { get; set; } = "";
        public string EntityRef { get; set; } = "";
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset? SnoozedUntil { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public Guid? DecidedByKey { get; set; }
        public string? Comment { get; set; }
        public int Rank { get; set; }
        public string Status { get; set; } = "";
    }
}
