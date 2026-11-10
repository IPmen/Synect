using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Domain.Banking;
using Synect.Domain.Edo;
using Synect.Domain.Reconciliation;

namespace Synect.Application.Reconciliation;

/// <summary>Which counterparties a recalculation covers. INNs are recalculated with their whole history.</summary>
public sealed record ReconciliationScope(ReconciliationScopeKind Kind, IReadOnlyCollection<string> Inns)
{
    public static ReconciliationScope Full { get; } = new(ReconciliationScopeKind.Full, []);

    /// <summary>Null and blank INNs mean payments without an INN (only their findings are refreshed).</summary>
    public static ReconciliationScope ForInns(IEnumerable<string?> inns) =>
        new(ReconciliationScopeKind.Inns, inns.Select(i => string.IsNullOrWhiteSpace(i) ? string.Empty : i.Trim()).Distinct().Order(StringComparer.Ordinal).ToList());
}

public sealed record ReconciliationRunResult(
    Guid RunId, string RulesVersion, int Inns, int Payments, int Documents, int Matches, int Created, int Removed, int Kept,
    int Findings, TimeSpan Elapsed);

/// <summary>
/// Recalculates the reconciliation of the current tenant (tenant from the context, RLS) from the database only.
/// Idempotent: a run over unchanged data keeps every stored Proposed match (same signature, same id) and changes nothing.
/// Confirmed and Rejected matches are never changed or removed by a run (only a person changes them, see
/// <see cref="ReconciliationReviewService"/>): amounts of Confirmed matches are subtracted before
/// matching, pairs of Rejected matches are never proposed again. Runs of one tenant are serialized with a PostgreSQL
/// advisory lock (also taken by confirm/reject), so a decision cannot interleave with a recalculation.
/// </summary>
public sealed class ReconciliationService(
    ISynectDbContext db,
    ITenantContext tenant,
    IOptions<ReconciliationOptions> options,
    IOptions<AnalyticsOptions> analytics,
    TimeProvider time,
    ILogger<ReconciliationService> logger)
{
    private const int Chunk = 2000;

    public static readonly JsonSerializerOptions ReasonJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Recalculates the scope. Joins the current transaction (after a load) or opens one.</summary>
    public Task<ReconciliationRunResult> ReconcileAsync(ReconciliationScope scope, CancellationToken ct) =>
        db.InTransactionAsync(token => RunAsync(scope, rebuild: false, token), ct);

    /// <summary>Drops every Proposed match and finding of the tenant and recalculates everything (Confirmed and Rejected stay).</summary>
    public Task<ReconciliationRunResult> RebuildAsync(CancellationToken ct) =>
        db.InTransactionAsync(token => RunAsync(ReconciliationScope.Full, rebuild: true, token), ct);

    /// <summary>Serializes reconciliation writes of one tenant (no-op outside PostgreSQL).</summary>
    internal static async Task LockTenantAsync(ISynectDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        var key = "synect.reconciliation:" + tenantId.ToString("N");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private async Task<ReconciliationRunResult> RunAsync(ReconciliationScope scope, bool rebuild, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Reconciliation requires a tenant context.");
        var o = options.Value;
        var stopwatch = Stopwatch.StartNew();
        // A load that changed nothing has nothing to recalculate: no run is recorded.
        if (scope.Kind == ReconciliationScopeKind.Inns && scope.Inns.Count == 0)
            return new ReconciliationRunResult(Guid.Empty, o.RulesVersion, 0, 0, 0, 0, 0, 0, 0, 0, stopwatch.Elapsed);
        await LockTenantAsync(db, tenantId, ct);

        var run = new ReconciliationRun
        {
            StartedAt = time.GetUtcNow(),
            Scope = scope.Kind,
            ScopeInns = scope.Kind == ReconciliationScopeKind.Inns ? scope.Inns.ToArray() : null,
            RulesVersion = o.RulesVersion,
        };
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        if (rebuild)
        {
            await db.ReconciliationMatches.Where(m => m.Status == MatchStatus.Proposed).ExecuteDeleteAsync(ct);
            await db.ReconciliationFindings.ExecuteDeleteAsync(ct);
        }

        var full = scope.Kind == ReconciliationScopeKind.Full;
        var inns = scope.Inns.ToList();

        var payments = await LoadPaymentsAsync(full, inns, ct);
        var documents = await LoadDocumentsAsync(full, inns, ct);
        var paymentIds = payments.Select(p => p.Id).ToList();
        var documentIds = documents.Select(d => d.Id).ToList();

        // Decisions people made on these payments and documents.
        var decided = new List<ReconciliationMatchItem>();
        foreach (var ids in paymentIds.Concat(documentIds).Chunk(Chunk))
            decided.AddRange(await db.ReconciliationMatchItems.AsNoTracking()
                .Where(i => i.MatchStatus != MatchStatus.Proposed && ids.Contains(i.RefId)).ToListAsync(ct));
        var locks = decided.Where(i => i.MatchStatus == MatchStatus.Confirmed)
            .Select(i => new AllocationLock(i.Kind, i.RefId, i.AllocatedAmountMinor)).ToList();
        var rejectedIds = decided.Where(i => i.MatchStatus == MatchStatus.Rejected).Select(i => i.MatchId).Distinct().ToList();
        var rejectedPairs = new HashSet<(Guid, Guid)>();
        foreach (var ids in rejectedIds.Chunk(Chunk))
        {
            var items = await db.ReconciliationMatchItems.AsNoTracking().Where(i => ids.Contains(i.MatchId)).ToListAsync(ct);
            foreach (var g in items.GroupBy(i => i.MatchId))
                foreach (var p in g.Where(i => i.Kind == MatchItemKind.BankTransaction))
                    foreach (var d in g.Where(i => i.Kind == MatchItemKind.EdoDocument))
                        rejectedPairs.Add((p.RefId, d.RefId));
        }

        // Stored Proposed matches of the scope, and any other Proposed match holding one of these items (stale INN).
        var existing = full
            ? await db.ReconciliationMatches.Where(m => m.Status == MatchStatus.Proposed).ToListAsync(ct)
            : await ProposedForInnsAsync(inns, ct);
        var known = existing.Select(m => m.Id).ToHashSet();
        foreach (var ids in paymentIds.Concat(documentIds).Chunk(Chunk))
        {
            var others = await db.ReconciliationMatchItems.AsNoTracking()
                .Where(i => i.MatchStatus == MatchStatus.Proposed && ids.Contains(i.RefId)).Select(i => i.MatchId).Distinct().ToListAsync(ct);
            var missing = others.Where(id => !known.Contains(id)).ToList();
            if (missing.Count == 0) continue;
            existing.AddRange(await db.ReconciliationMatches.Where(m => missing.Contains(m.Id)).ToListAsync(ct));
            known.UnionWith(missing);
        }

        // Run the rules per counterparty.
        var engine = new ReconciliationEngine(o, PurposeParser.Default);
        var paymentsByInn = payments.ToLookup(p => p.Inn);
        var documentsByInn = documents.ToLookup(d => d.Inn);
        var allInns = full
            ? paymentsByInn.Select(g => g.Key).Concat(documentsByInn.Select(g => g.Key)).Distinct().Order(StringComparer.Ordinal).ToList()
            : inns;
        var proposed = new List<ProposedMatch>();
        var findings = new List<ProposedFinding>();
        var lockLookup = locks.ToLookup(l => l.RefId);
        foreach (var inn in allInns)
        {
            var innPayments = paymentsByInn[inn].ToList();
            var innDocuments = documentsByInn[inn].ToList();
            var innLocks = innPayments.Select(p => p.Id).Concat(innDocuments.Select(d => d.Id)).SelectMany(id => lockLookup[id]).ToList();
            var result = engine.Run(inn, innPayments, innDocuments, innLocks, rejectedPairs);
            proposed.AddRange(result.Matches);
            findings.AddRange(result.Findings);
        }

        // Keep stored matches with the same signature; remove the rest; add the new ones.
        var now = time.GetUtcNow();
        var bySignature = existing.GroupBy(m => m.Signature).ToDictionary(g => g.Key, g => g.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToList());
        var kept = 0;
        var newMatches = new List<(ReconciliationMatch Match, ProposedMatch Source)>();
        foreach (var m in proposed)
        {
            var signature = m.Signature;
            var reasons = JsonSerializer.Serialize(m.Reasons, ReasonJson);
            if (bySignature.TryGetValue(signature, out var same) && same.Count > 0)
            {
                var row = same[0];
                same.RemoveAt(0);
                kept++;
                if (row.Confidence != m.Confidence || row.Reasons != reasons)
                {
                    row.Confidence = m.Confidence;
                    row.Reasons = reasons;
                    row.RunId = run.Id;
                    row.UpdatedAt = now;
                }
                continue;
            }
            newMatches.Add((new ReconciliationMatch
            {
                RunId = run.Id, MatchType = m.Type, Status = MatchStatus.Proposed, Confidence = m.Confidence, Reasons = reasons,
                CounterpartyInn = m.Inn, Currency = m.Currency, DocumentOwner = m.Owner, PaymentsTotal = m.PaymentsTotal,
                DocumentsTotal = m.DocumentsTotal, Signature = signature, CreatedAt = now, UpdatedAt = now,
            }, m));
        }
        var removed = bySignature.Values.SelectMany(x => x).Select(m => m.Id).ToList();
        foreach (var ids in removed.Chunk(Chunk))
        {
            await db.ReconciliationMatchItems.Where(i => ids.Contains(i.MatchId)).ExecuteDeleteAsync(ct);
            await db.ReconciliationMatches.Where(m => ids.Contains(m.Id) && m.Status == MatchStatus.Proposed).ExecuteDeleteAsync(ct);
        }
        foreach (var row in existing.Where(m => removed.Contains(m.Id)))
            db.ReconciliationMatches.Entry(row).State = EntityState.Detached;
        await db.SaveChangesAsync(ct); // kept rows: new confidence and reasons

        foreach (var (match, source) in newMatches)
        {
            db.ReconciliationMatches.Add(match);
            foreach (var p in source.Payments)
                db.ReconciliationMatchItems.Add(new ReconciliationMatchItem
                    { MatchId = match.Id, Kind = MatchItemKind.BankTransaction, RefId = p.RefId, AllocatedAmountMinor = p.Amount, MatchStatus = MatchStatus.Proposed });
            foreach (var d in source.Documents)
                db.ReconciliationMatchItems.Add(new ReconciliationMatchItem
                    { MatchId = match.Id, Kind = MatchItemKind.EdoDocument, RefId = d.RefId, AllocatedAmountMinor = d.Amount, MatchStatus = MatchStatus.Proposed });
        }

        // Findings are derived: replaced for every recalculated INN.
        if (full) await db.ReconciliationFindings.ExecuteDeleteAsync(ct);
        else
            foreach (var chunk in inns.Chunk(Chunk))
                await db.ReconciliationFindings.Where(f => chunk.Contains(f.CounterpartyInn)).ExecuteDeleteAsync(ct);
        foreach (var f in findings)
            db.ReconciliationFindings.Add(new ReconciliationFinding
            {
                RunId = run.Id, Kind = f.Kind, CounterpartyInn = f.Inn, SubjectKind = f.SubjectKind, SubjectId = f.SubjectId,
                RelatedKind = f.RelatedKind, RelatedId = f.RelatedId, Confidence = f.Confidence,
                Reasons = JsonSerializer.Serialize(f.Reasons, ReasonJson), CreatedAt = now,
            });

        return await FinishAsync(run, allInns.Count, payments.Count, documents.Count, proposed.Count, newMatches.Count, removed.Count, kept,
            findings.Count, stopwatch);
    }

    private async Task<ReconciliationRunResult> FinishAsync(
        ReconciliationRun run, int inns, int payments, int documents, int matches, int created, int removed, int kept, int findings, Stopwatch stopwatch)
    {
        run.FinishedAt = time.GetUtcNow();
        run.MatchCount = matches;
        run.CreatedCount = created;
        run.RemovedCount = removed;
        // The change tracker is not cleared: after a load this runs inside the load's transaction, which saves more later.
        await db.SaveChangesAsync(CancellationToken.None);
        var result = new ReconciliationRunResult(run.Id, run.RulesVersion, inns, payments, documents, matches, created, removed, kept, findings, stopwatch.Elapsed);
        logger.LogInformation(
            "Reconciliation {Scope}: {Inns} INNs, {Payments} payments, {Documents} documents, {Matches} proposed matches ({Created} new, {Removed} removed), {Findings} findings, {ElapsedMs} ms",
            run.Scope, inns, payments, documents, matches, created, removed, findings, (long)stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private async Task<List<ReconciliationMatch>> ProposedForInnsAsync(List<string> inns, CancellationToken ct)
    {
        var result = new List<ReconciliationMatch>();
        foreach (var chunk in inns.Chunk(Chunk))
            result.AddRange(await db.ReconciliationMatches.Where(m => m.Status == MatchStatus.Proposed && chunk.Contains(m.CounterpartyInn)).ToListAsync(ct));
        return result;
    }

    private async Task<List<PaymentInput>> LoadPaymentsAsync(bool full, List<string> inns, CancellationToken ct)
    {
        var result = new List<PaymentInput>();
        foreach (var chunk in full ? [null] : inns.Chunk(Chunk).Select(c => c.ToList()))
        {
            var query = db.BankTransactions.AsNoTracking().WithoutErrors();
            if (chunk is not null) query = query.Where(t => chunk.Contains(t.CounterpartyInn ?? ""));
            var rows = await query
                .Select(t => new { t.Id, t.CounterpartyInn, t.Direction, t.Amount, t.Currency, t.PostingDate, t.Purpose, t.CounterpartyAccount })
                .ToListAsync(ct);
            result.AddRange(rows.Select(t => new PaymentInput(t.Id, (t.CounterpartyInn ?? "").Trim(), t.Direction, t.Amount, t.Currency,
                t.PostingDate, t.Purpose, t.CounterpartyAccount)));
        }
        return result;
    }

    private async Task<List<DocumentInput>> LoadDocumentsAsync(bool full, List<string> inns, CancellationToken ct)
    {
        var counted = analytics.Value.Edo.CountedStatuses;
        var types = options.Value.DocumentTypes;
        var offset = analytics.Value.UtcOffset;
        var result = new List<DocumentInput>();
        foreach (var chunk in full ? [null] : inns.Where(i => i.Length > 0).Chunk(Chunk).Select(c => c.ToList()))
        {
            var query = db.EdoDocuments.AsNoTracking()
                .Where(d => counted.Contains(d.Status) && types.Contains(d.DocType) && d.CounterpartyInn != null && d.CounterpartyInn != "");
            if (chunk is not null) query = query.Where(d => chunk.Contains(d.CounterpartyInn!));
            var rows = await query.Select(d => new
            {
                d.Id, d.CounterpartyInn, d.Owner, d.DocType, d.Number, d.DocumentDate, d.SourceCreatedAt, d.ContractNumber,
                d.TotalDeliverySumWithVat, d.TotalSum, d.Currency, d.SellerAccount,
            }).ToListAsync(ct);
            result.AddRange(rows.Select(d => new DocumentInput(d.Id, d.CounterpartyInn!.Trim(), d.Owner, d.DocType, d.Number,
                d.DocumentDate ?? (d.SourceCreatedAt is { } c ? DateOnly.FromDateTime(c.ToOffset(offset).DateTime) : null),
                d.ContractNumber, d.TotalDeliverySumWithVat > 0 ? d.TotalDeliverySumWithVat : d.TotalSum, d.Currency, d.SellerAccount)));
        }
        return result;
    }
}

public enum ReviewOutcome
{
    Confirmed,
    Rejected,

    /// <summary>The match already had the requested status (nothing changed, no history row).</summary>
    Unchanged,

    /// <summary>
    /// Confirming a Rejected match would allocate a payment or a document beyond its amount, because other Confirmed
    /// matches already use it. Nothing changed; one of those matches has to be rejected first.
    /// </summary>
    Conflict,

    NotFound,
}

/// <param name="PreviousStatus">Status before this call (equal to Status when unchanged).</param>
/// <param name="ConflictingMatches">For <see cref="ReviewOutcome.Conflict"/>: the Confirmed matches that already use the items.</param>
public sealed record ReviewResult(
    ReviewOutcome Outcome, Guid MatchId, MatchStatus? Status, DateTimeOffset? DecidedAt,
    MatchStatus? PreviousStatus = null, Guid? DecisionId = null, IReadOnlyList<Guid>? ConflictingMatches = null);

/// <summary>
/// Confirm or reject a match. Changes only Synect's reconciliation tables (never a source). A Proposed match can be
/// confirmed or rejected; a decided match can be changed between Confirmed and Rejected at any time (a person's
/// decision is not irreversible). Every change is appended to reconciliation_match_decisions (who, when, previous and new
/// status, comment); the match keeps the latest decision. Recalculation never changes Confirmed or Rejected matches.
/// After a change of a decided match the counterparty (INN) is recalculated in the same transaction, so that Proposed
/// matches reflect the new decision at once (released items can be proposed again, newly confirmed items are taken out
/// of other Proposed matches).
/// </summary>
public sealed class ReconciliationReviewService(
    ISynectDbContext db, ITenantContext tenant, TimeProvider time, ReconciliationService reconciliation, Attention.AttentionTrigger attention)
{
    public Task<ReviewResult> ConfirmAsync(Guid matchId, Guid? apiKeyId, string? comment, CancellationToken ct) =>
        DecideAsync(matchId, MatchStatus.Confirmed, apiKeyId, comment, ct);

    public Task<ReviewResult> RejectAsync(Guid matchId, Guid? apiKeyId, string? comment, CancellationToken ct) =>
        DecideAsync(matchId, MatchStatus.Rejected, apiKeyId, comment, ct);

    private async Task<ReviewResult> DecideAsync(Guid matchId, MatchStatus decision, Guid? apiKeyId, string? comment, CancellationToken ct)
    {
        var result = await DecideInTransactionAsync(matchId, decision, apiKeyId, comment, ct);
        // Reconciliation attention rules (double payments, other accounts, unmatched payments, unpaid acts) after the change.
        if (result.Outcome is ReviewOutcome.Confirmed or ReviewOutcome.Rejected && db.Database.CurrentTransaction is null)
            await attention.AfterReconciliationAsync(CancellationToken.None);
        return result;
    }

    private Task<ReviewResult> DecideInTransactionAsync(Guid matchId, MatchStatus decision, Guid? apiKeyId, string? comment, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Reconciliation review requires a tenant context.");
            await ReconciliationService.LockTenantAsync(db, tenantId, token);
            var match = await db.ReconciliationMatches.SingleOrDefaultAsync(m => m.Id == matchId, token);
            if (match is null) return new ReviewResult(ReviewOutcome.NotFound, matchId, null, null);
            var previous = match.Status;
            if (previous == decision) return new ReviewResult(ReviewOutcome.Unchanged, matchId, previous, match.DecidedAt, previous);

            if (decision == MatchStatus.Confirmed && previous == MatchStatus.Rejected
                && await OverAllocatedByAsync(matchId, token) is { Count: > 0 } conflicts)
                return new ReviewResult(ReviewOutcome.Conflict, matchId, previous, match.DecidedAt, previous, ConflictingMatches: conflicts);

            var now = time.GetUtcNow();
            var text = string.IsNullOrWhiteSpace(comment) ? null
                : comment.Trim() is { Length: > ReconciliationMatch.MaxCommentLength } long_ ? long_[..ReconciliationMatch.MaxCommentLength] : comment.Trim();
            match.Status = decision;
            match.DecidedAt = now;
            match.DecidedByKey = apiKeyId;
            match.DecisionComment = text;
            match.UpdatedAt = now;
            var history = new ReconciliationMatchDecision
            {
                MatchId = matchId, PreviousStatus = previous, NewStatus = decision, DecidedAt = now, DecidedByKey = apiKeyId, Comment = text,
            };
            db.ReconciliationMatchDecisions.Add(history);
            await db.ReconciliationMatchItems.Where(i => i.MatchId == matchId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.MatchStatus, decision), token);
            await db.SaveChangesAsync(token);

            if (previous != MatchStatus.Proposed)
                await reconciliation.ReconcileAsync(ReconciliationScope.ForInns([match.CounterpartyInn]), token);

            return new ReviewResult(decision == MatchStatus.Confirmed ? ReviewOutcome.Confirmed : ReviewOutcome.Rejected,
                matchId, decision, now, previous, history.Id);
        }, ct);

    /// <summary>
    /// Other Confirmed matches that, together with this match, would allocate more than the full amount of one of its
    /// payments or documents (a Rejected match was computed when those items were free, so they may be used since).
    /// </summary>
    private async Task<List<Guid>> OverAllocatedByAsync(Guid matchId, CancellationToken ct)
    {
        var own = await db.ReconciliationMatchItems.AsNoTracking().Where(i => i.MatchId == matchId).ToListAsync(ct);
        var refs = own.Select(i => i.RefId).ToList();
        var others = await db.ReconciliationMatchItems.AsNoTracking()
            .Where(i => i.MatchId != matchId && i.MatchStatus == MatchStatus.Confirmed && refs.Contains(i.RefId)).ToListAsync(ct);
        if (others.Count == 0) return [];

        var paymentIds = own.Where(i => i.Kind == MatchItemKind.BankTransaction).Select(i => i.RefId).ToList();
        var documentIds = own.Where(i => i.Kind == MatchItemKind.EdoDocument).Select(i => i.RefId).ToList();
        var full = new Dictionary<Guid, long>();
        foreach (var p in await db.BankTransactions.AsNoTracking().Where(t => paymentIds.Contains(t.Id))
                     .Select(t => new { t.Id, t.Amount }).ToListAsync(ct))
            full[p.Id] = p.Amount;
        foreach (var d in await db.EdoDocuments.AsNoTracking().Where(x => documentIds.Contains(x.Id))
                     .Select(x => new { x.Id, x.TotalDeliverySumWithVat, x.TotalSum }).ToListAsync(ct))
            full[d.Id] = d.TotalDeliverySumWithVat > 0 ? d.TotalDeliverySumWithVat : d.TotalSum;

        var conflicts = new HashSet<Guid>();
        foreach (var item in own)
        {
            var used = others.Where(o => o.RefId == item.RefId && o.Kind == item.Kind).ToList();
            if (used.Count == 0) continue;
            var total = item.AllocatedAmountMinor + used.Sum(o => o.AllocatedAmountMinor);
            if (!full.TryGetValue(item.RefId, out var amount) || total > amount)
                conflicts.UnionWith(used.Select(o => o.MatchId));
        }
        return conflicts.Order().ToList();
    }
}
