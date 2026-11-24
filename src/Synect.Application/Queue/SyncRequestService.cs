using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Domain.Sync;

namespace Synect.Application.Queue;

public enum SyncRequestStatus
{
    /// <summary>A new job was queued ("started").</summary>
    Queued,

    /// <summary>A job was already waiting; it was moved to the front.</summary>
    AlreadyQueued,

    /// <summary>A load of this connection is in progress.</summary>
    AlreadyRunning,

    /// <summary>The previous request was too recent; see RetryAfter.</summary>
    TooSoon,

    /// <summary>The connection is NeedsAttention or Disabled.</summary>
    ConnectionNotActive,

    /// <summary>The connection gets its data from imported statement files; there is nothing to load.</summary>
    NotLoadable,
}

public sealed record SyncRequestResult(SyncRequestStatus Status, Guid? JobId = null, TimeSpan? RetryAfter = null, string? Reason = null);

public enum ReactivationStatus
{
    Reactivated,
    AlreadyActive,
    TestFailed,
    Disabled,
}

public sealed record ReactivationResult(ReactivationStatus Status, string? Error = null, Guid? JobId = null);

/// <summary>User-facing commands on connections. Never waits for a load to finish.</summary>
public sealed class SyncRequestService(
    ISynectDbContext db,
    SyncJobQueue queue,
    IAdapterRegistry registry,
    ICredentialProvider credentials,
    AdapterCallPolicy policy,
    OData.ODataSyncService odata,
    TimeProvider time,
    IOptions<SyncQueueOptions> options,
    ILogger<SyncRequestService> logger)
{
    /// <summary>"Refresh now": queues a high-priority load and returns immediately.</summary>
    public Task<SyncRequestResult> RequestSyncAsync(Guid connectionId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var connection = await LoadAsync(connectionId, token);
            if (!connection.IsSyncable)
                return new SyncRequestResult(SyncRequestStatus.NotLoadable,
                    Reason: connection.IsOneC
                        ? "Data of this connection is sent by the 1C extension; use «Отправить сейчас» in 1C to refresh it."
                        : "Data of this connection comes from imported bank statement files; import a newer statement to refresh it.");
            if (connection.Status != ConnectionStatus.Active)
                return new SyncRequestResult(SyncRequestStatus.ConnectionNotActive, Reason: connection.StatusReason);

            var now = time.GetUtcNow();
            if (await queue.ActiveJobAsync(connectionId, token) is { } active)
            {
                if (active.Status == SyncJobStatus.Running)
                    return new SyncRequestResult(SyncRequestStatus.AlreadyRunning, active.Id);
                active.Expedite(now);
                await db.SaveChangesAsync(token);
                return new SyncRequestResult(SyncRequestStatus.AlreadyQueued, active.Id);
            }

            var lastManual = await db.SyncJobs
                .Where(j => j.ConnectionId == connectionId && j.Kind == SyncJobKind.Manual)
                .OrderByDescending(j => j.EnqueuedAt)
                .Select(j => (DateTimeOffset?)j.EnqueuedAt)
                .FirstOrDefaultAsync(token);
            var nextAllowed = lastManual + options.Value.ManualRequestMinInterval;
            if (nextAllowed is { } allowedAt && allowedAt > now)
                return new SyncRequestResult(SyncRequestStatus.TooSoon, RetryAfter: allowedAt - now);

            var (job, created) = await queue.EnqueueAsync(connection, SyncJobKind.Manual, null, token);
            return created
                ? new SyncRequestResult(SyncRequestStatus.Queued, job.Id)
                : new SyncRequestResult(job.Status == SyncJobStatus.Running ? SyncRequestStatus.AlreadyRunning : SyncRequestStatus.AlreadyQueued, job.Id);
        }, ct);

    /// <summary>
    /// Brings a NeedsAttention connection back: tests the connection and, on success, makes it Active
    /// and queues its first load. A failed test keeps it in NeedsAttention with the new reason.
    /// </summary>
    public async Task<ReactivationResult> ReactivateAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await db.InTransactionAsync(token => LoadAsync(connectionId, token), ct);
        switch (connection.Status)
        {
            case ConnectionStatus.Disabled:
                return new ReactivationResult(ReactivationStatus.Disabled, connection.StatusReason);
            case ConnectionStatus.Active:
                return new ReactivationResult(ReactivationStatus.AlreadyActive);
        }

        var context = SyncRunFinalizer.CreateAdapterContext(connection, credentials);
        ConnectionTestResult test;
        try
        {
            test = connection.AdapterKind switch
            {
                AdapterKind.Bank => await TestAsync(registry.GetBank(connection.SourceCode), context, ct),
                AdapterKind.Edo => await TestAsync(registry.GetEdo(connection.SourceCode), context, ct),
                AdapterKind.Accounting1C when connection.IsOneCOData => await odata.TestAsync(connection.Id, ct),
                _ => ConnectionTestResult.Failed($"Adapter kind {connection.AdapterKind} is not supported yet."),
            };
        }
        catch (AdapterException ex)
        {
            test = ConnectionTestResult.Failed(ex.Message);
        }

        return await db.InTransactionAsync(async token =>
        {
            var tracked = await LoadAsync(connectionId, token);
            var now = time.GetUtcNow();
            if (!test.Success)
            {
                tracked.MarkNeedsAttention("Connection test failed: " + test.Error, now);
                await db.SaveChangesAsync(token);
                logger.LogInformation("Reactivation of connection {ConnectionId} failed the connection test", connectionId);
                return new ReactivationResult(ReactivationStatus.TestFailed, test.Error);
            }

            tracked.Activate(now);
            await db.SaveChangesAsync(token);
            var kind = tracked.LastSuccessfulSyncAt is null ? SyncJobKind.Initial : SyncJobKind.Reactivation;
            var (job, _) = await queue.EnqueueAsync(tracked, kind, null, token);
            logger.LogInformation("Connection {ConnectionId} reactivated, job {JobId} queued", connectionId, job.Id);
            return new ReactivationResult(ReactivationStatus.Reactivated, JobId: job.Id);
        }, ct);
    }

    private Task<ConnectionTestResult> TestAsync(IBankAdapter adapter, AdapterContext context, CancellationToken ct) =>
        policy.ExecuteAsync(adapter, context, "TestConnection", token => adapter.TestConnectionAsync(context, token), ct);

    private Task<ConnectionTestResult> TestAsync(IEdoAdapter adapter, AdapterContext context, CancellationToken ct) =>
        policy.ExecuteAsync(adapter, context, "TestConnection", token => adapter.TestConnectionAsync(context, token), ct);

    private async Task<Connection> LoadAsync(Guid connectionId, CancellationToken ct) =>
        await db.Connections.SingleOrDefaultAsync(c => c.Id == connectionId, ct)
        ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
}
