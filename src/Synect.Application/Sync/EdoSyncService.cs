using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Domain.Edo;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

public sealed class EdoSyncOptions
{
    /// <summary>
    /// The incremental window starts this long before the last successful sync, to tolerate
    /// clock skew and late updates on the provider side. Re-reading is idempotent.
    /// </summary>
    public TimeSpan UpdatedSinceOverlap { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Safety limit against a provider that never reports the last page.</summary>
    public int MaxPagesPerOwner { get; set; } = 10_000;
}

/// <summary>
/// Loads EDO documents (incoming and outgoing) of one connection. Records every observed
/// status change in the append-only <see cref="EdoDocumentStatusLog"/>.
/// </summary>
public sealed class EdoSyncService(
    ISynectDbContext db,
    IAdapterRegistry registry,
    RawObjectStore rawStore,
    CounterpartyStore counterparties,
    ICredentialProvider credentials,
    AdapterCallPolicy policy,
    TimeProvider time,
    IOptions<EdoSyncOptions> options,
    IOptions<CircuitBreakerOptions> breakerOptions,
    ILogger<EdoSyncService> logger)
{
    private static readonly EdoDocumentOwner[] Owners = [EdoDocumentOwner.Incoming, EdoDocumentOwner.Outgoing];

    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        var (connection, run) = await db.InTransactionAsync(async token =>
        {
            var c = await db.Connections.SingleOrDefaultAsync(x => x.Id == connectionId, token)
                ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
            if (c.AdapterKind != AdapterKind.Edo)
                throw new InvalidOperationException($"Connection {connectionId} is not an EDO connection.");
            if (c.Status == ConnectionStatus.Disabled)
                throw new InvalidOperationException($"Connection {connectionId} is disabled.");

            var r = SyncRun.Start(c.TenantId, c.Id, time.GetUtcNow());
            db.SyncRuns.Add(r);
            await db.SaveChangesAsync(token);
            return (c, r);
        }, ct);

        var adapter = registry.GetEdo(connection.SourceCode);
        var context = SyncRunFinalizer.CreateAdapterContext(connection, credentials);
        DateTimeOffset? updatedSince = adapter.Capabilities.SupportsUpdatedSince && connection.LastSuccessfulSyncAt is { } last
            ? last - options.Value.UpdatedSinceOverlap
            : null;
        var records = 0;
        var complete = true;

        logger.LogInformation("EDO sync started: connection {ConnectionId}, source {SourceCode}, updated since {UpdatedSince}",
            connection.Id, connection.SourceCode, updatedSince);

        try
        {
            foreach (var owner in Owners)
            {
                var page = 1;
                while (true)
                {
                    var currentPage = page;
                    var result = await policy.ExecuteAsync(adapter, context, "ListDocuments",
                        token => adapter.ListDocumentsAsync(context, owner, updatedSince, currentPage, token), ct);
                    records += result.Items.Count;
                    await db.InTransactionAsync(async token =>
                    {
                        await UpsertDocumentsAsync(connection, run, result.Items, token);
                        await db.SaveChangesAsync(token);
                    }, ct);

                    if (!result.HasMore) break;
                    if (++page > options.Value.MaxPagesPerOwner)
                    {
                        complete = false;
                        logger.LogWarning("EDO sync stopped at page limit: connection {ConnectionId}, owner {Owner}",
                            connection.Id, owner);
                        break;
                    }
                }
            }

            await db.InTransactionAsync(async token =>
            {
                var now = time.GetUtcNow();
                run.Succeed(now, complete, records);
                connection.MarkSyncSucceeded(now);
                await db.SaveChangesAsync(token);
            }, ct);

            logger.LogInformation("EDO sync succeeded: connection {ConnectionId}, records {Records}", connection.Id, records);
            return new SyncOutcome(run.Id, run.Result, run.IsComplete, records, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("EDO sync failed: connection {ConnectionId}, error type {ErrorType}",
                connection.Id, ex.GetType().Name);
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connection.Id, ex, records, time.GetUtcNow(),
                breakerOptions.Value.FailureThreshold);
            if (ex is OperationCanceledException) throw;
            return outcome;
        }
    }

    private async Task UpsertDocumentsAsync(
        Connection connection, SyncRun run, IReadOnlyList<EdoDocumentInfo> infos, CancellationToken ct)
    {
        if (infos.Count == 0) return;
        var now = time.GetUtcNow();

        var items = infos.GroupBy(i => i.ExternalId).Select(g => g.Last()).ToList();
        var hashes = await rawStore.UpsertAsync(connection, items.Select(i => i.Raw).ToList(), now, ct);

        var ids = items.Select(i => i.ExternalId).ToList();
        var existing = await db.EdoDocuments
            .Where(d => d.ConnectionId == connection.Id && ids.Contains(d.ExternalId))
            .ToDictionaryAsync(d => d.ExternalId, StringComparer.Ordinal, ct);

        for (var i = 0; i < items.Count; i++)
        {
            var info = items[i];
            var hash = hashes[i];
            if (!existing.TryGetValue(info.ExternalId, out var doc))
            {
                doc = new EdoDocument
                {
                    TenantId = connection.TenantId,
                    ConnectionId = connection.Id,
                    ExternalId = info.ExternalId,
                    CreatedAt = now,
                };
                db.EdoDocuments.Add(doc);
            }
            else if (doc.SourceHash == hash)
            {
                continue;
            }

            doc.DocType = info.DocType;
            doc.Owner = info.Owner;
            doc.Number = info.Number;
            doc.DocumentDate = info.DocumentDate;
            doc.CounterpartyInn = info.CounterpartyInn;
            doc.CounterpartyName = info.CounterpartyName;
            doc.ContractNumber = info.ContractNumber;
            doc.ContractDate = info.ContractDate;
            doc.TotalSum = info.TotalSum;
            doc.TotalVatSum = info.TotalVatSum;
            doc.TotalDeliverySum = info.TotalDeliverySum;
            doc.TotalDeliverySumWithVat = info.TotalDeliverySumWithVat;
            doc.SourceCreatedAt = info.CreatedAt;
            doc.SourceUpdatedAt = info.UpdatedAt;
            doc.SourceHash = hash;
            doc.UpdatedAt = now;

            var log = doc.ApplyStatus(info.Status, now, run.Id);
            if (log is not null) db.EdoDocumentStatusLogs.Add(log);
        }

        await counterparties.UpsertAsync(connection.TenantId,
            items.Select(i => (i.CounterpartyInn, i.CounterpartyName)), now, ct);
    }
}
