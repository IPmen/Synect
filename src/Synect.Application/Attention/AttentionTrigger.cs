using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Attention;
using Synect.Domain.Sync;

namespace Synect.Application.Attention;

/// <summary>When each tenant last had a full evaluation (per process): drives the periodic run of time-based rules.</summary>
public sealed class AttentionSchedule
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFull = new();

    public bool IsDue(Guid tenantId, DateTimeOffset now, TimeSpan interval) =>
        !_lastFull.TryGetValue(tenantId, out var last) || now - last >= interval;

    public void MarkFull(Guid tenantId, DateTimeOffset at) => _lastFull[tenantId] = at;
}

/// <summary>
/// Runs the attention rules after the events that can change their result, in a separate DI scope and transaction of
/// the same tenant: after a successful load (connections, its data and the reconciliation), after a failed load
/// (connections), after a reconciliation decision, and periodically for time-based rules (Attention:PeriodicInterval).
/// A failed evaluation is logged and never fails the load or the decision. Only on PostgreSQL (the rules use its SQL);
/// elsewhere (SQLite logic tests) it does nothing.
/// </summary>
public sealed class AttentionTrigger(
    ISynectDbContext db,
    ITenantContext tenant,
    TenantScopeRunner scopes,
    AttentionSchedule schedule,
    IOptions<AttentionOptions> options,
    TimeProvider time,
    ILogger<AttentionTrigger> logger)
{
    public Task<AttentionEvaluation?> AfterLoadAsync(AdapterKind kind, bool succeeded, CancellationToken ct)
    {
        if (!succeeded) return RunAsync([AttentionCategory.Connection], ct);
        var data = kind == AdapterKind.Edo ? AttentionCategory.Documents : AttentionCategory.Bank;
        return RunAsync([AttentionCategory.Connection, data, AttentionCategory.Reconciliation], ct);
    }

    public Task<AttentionEvaluation?> AfterReconciliationAsync(CancellationToken ct) => RunAsync([AttentionCategory.Reconciliation], ct);

    /// <summary>Every rule; due at most once per Attention:PeriodicInterval per tenant (unless <paramref name="force"/>).</summary>
    public async Task<AttentionEvaluation?> PeriodicAsync(CancellationToken ct, bool force = false)
    {
        if (tenant.TenantId is not { } tenantId) return null;
        if (!force && !schedule.IsDue(tenantId, time.GetUtcNow(), options.Value.PeriodicInterval)) return null;
        return await RunAsync(AttentionEvaluator.AllCategories, ct);
    }

    private async Task<AttentionEvaluation?> RunAsync(IReadOnlyList<AttentionCategory> categories, CancellationToken ct)
    {
        if (!options.Value.Enabled || db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return null;
        if (tenant.TenantId is not { } tenantId) return null;
        try
        {
            var result = await scopes.InTenantAsync(tenantId,
                (sp, token) => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync(categories, token), ct);
            if (categories.Count == AttentionEvaluator.AllCategories.Count) schedule.MarkFull(tenantId, time.GetUtcNow());
            if (result.Created + result.Reopened + result.Resolved > 0)
                logger.LogInformation("Attention evaluated for tenant {TenantId}: {Created} created, {Reopened} reopened, {Resolved} resolved, {Elapsed} ms",
                    tenantId, result.Created, result.Reopened, result.Resolved, (int)result.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Attention evaluation failed for tenant {TenantId}: {ErrorType}", tenantId, ex.GetType().Name);
            return null;
        }
    }
}
