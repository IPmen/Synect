using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Infrastructure;
using Synect.Infrastructure.Persistence;

namespace Synect.Worker;

/// <summary>
/// Hosts the sync queue.
/// 1. Single instance: takes a PostgreSQL advisory lock (<see cref="WorkerInstanceLock"/>); while another worker holds
///    it, this one takes no jobs and retries every Sync:Queue:InstanceLockRetryInterval.
/// 2. Waits until the schema is compatible with this build (migrations are a separate deployment step).
/// 3. Fails jobs and runs left Running by a previous process; then every Sync:Queue:PollInterval it recovers jobs whose
///    heartbeat stopped, plans scheduled loads and starts queued jobs (<see cref="SyncDispatcher"/>).
/// 4. Graceful stop: no new jobs; running loads get Sync:Queue:ShutdownDrainTimeout to finish, the rest are cancelled
///    and go back to Queued (<see cref="SyncDispatcher.DrainAsync"/>).
/// If the lock is lost (database connection gone), the worker stops with exit code 1 and systemd restarts it.
/// Sync:RunOnStartup (development only) additionally queues a load of every Active connection at startup.
/// </summary>
public sealed class SyncWorker(
    SyncDispatcher dispatcher,
    TenantScopeRunner tenants,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IOptions<SyncQueueOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SchemaRetryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Synect' is not configured.");
        await using var instanceLock = new WorkerInstanceLock(connectionString);

        try
        {
            if (!await AcquireLockAsync(instanceLock, stoppingToken)) return;
            if (!await WaitForCompatibleSchemaAsync(stoppingToken)) return;

            // Single worker instance (guaranteed by the lock): whatever is still Running belongs to a process that died.
            await RecoverAfterRestartAsync(stoppingToken);

            if (configuration.GetValue<bool>("Sync:RunOnStartup"))
                await QueueEverythingAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await instanceLock.IsHeldAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                logger.LogCritical("The single-instance lock of the worker was lost (database connection gone); stopping");
                Environment.ExitCode = 1;
                lifetime.StopApplication();
                break;
            }

            try
            {
                var started = await dispatcher.TickAsync(stoppingToken);
                if (started > 0) logger.LogInformation("Started {Count} sync job(s)", started);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Dispatcher tick failed: {ErrorType}", ex.GetType().Name);
            }

            try { await Task.Delay(options.Value.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        var returned = await dispatcher.DrainAsync(options.Value.ShutdownDrainTimeout);
        logger.LogInformation("Sync worker stopped; {Count} job(s) returned to the queue", returned);
    }

    private async Task<bool> AcquireLockAsync(WorkerInstanceLock instanceLock, CancellationToken ct)
    {
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await instanceLock.TryAcquireAsync(ct))
                {
                    logger.LogInformation("Single-instance lock of the worker acquired");
                    return true;
                }
                if (!warned) logger.LogWarning("Another sync worker holds the single-instance lock; this one takes no jobs and waits");
                warned = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Could not take the single-instance lock: {ErrorType}", ex.GetType().Name);
            }
            await Task.Delay(options.Value.InstanceLockRetryInterval, ct);
        }
        return false;
    }

    private async Task<bool> WaitForCompatibleSchemaAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var status = await scope.ServiceProvider.GetRequiredService<SchemaCompatibility>().CheckAsync(ct);
                if (status.Compatible)
                {
                    logger.LogInformation("Schema {Schema} is compatible", status.AppliedLatest);
                    return true;
                }
                if (status.DatabaseAvailable)
                    logger.LogError("Schema is not compatible: {Pending} pending migration(s), expected {Expected}; run `Synect.DevTools migrate`. No jobs are taken",
                        status.Pending.Count, status.ExpectedLatest);
                else
                    logger.LogError("Database is not available: {ErrorType}", status.Error);
            }
            await Task.Delay(SchemaRetryInterval, ct);
        }
        return false;
    }

    private async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        foreach (var tenantId in await tenants.TenantIdsAsync(ct))
        {
            var recovered = await tenants.InTenantAsync(tenantId,
                (sp, token) => sp.GetRequiredService<SyncRecovery>().RecoverAfterRestartAsync(token), ct);
            if (recovered > 0)
                logger.LogWarning("Marked {Count} job(s)/run(s) left Running by the previous process as Failed for tenant {TenantId}",
                    recovered, tenantId);
        }
    }

    private async Task QueueEverythingAsync(CancellationToken ct)
    {
        foreach (var tenantId in await tenants.TenantIdsAsync(ct))
        {
            var queued = await tenants.InTenantAsync(tenantId, async (sp, token) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                var queue = sp.GetRequiredService<SyncJobQueue>();
                return await db.InTransactionAsync(async t =>
                {
                    var count = 0;
                    var connections = await db.Connections
                        .Where(c => c.Status == ConnectionStatus.Active && (c.AdapterKind == AdapterKind.Bank || c.AdapterKind == AdapterKind.Edo))
                        .ToListAsync(t);
                    foreach (var connection in connections)
                        if ((await queue.EnqueueAsync(connection, SyncJobKind.Manual, null, t)).Created) count++;
                    return count;
                }, token);
            }, ct);
            logger.LogInformation("Sync:RunOnStartup queued {Count} load(s) for tenant {TenantId}", queued, tenantId);
        }
    }
}
