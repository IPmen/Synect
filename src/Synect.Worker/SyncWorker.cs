using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Sync;

namespace Synect.Worker;

/// <summary>
/// Hosts the sync queue: every Sync:Queue:PollInterval it recovers abandoned runs, plans scheduled
/// loads and starts queued jobs (<see cref="SyncDispatcher"/>). Sync:RunOnStartup (development only)
/// additionally queues a load of every Active connection at startup.
/// </summary>
public sealed class SyncWorker(
    SyncDispatcher dispatcher,
    TenantScopeRunner tenants,
    IConfiguration configuration,
    IOptions<SyncQueueOptions> options,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetValue<bool>("Sync:RunOnStartup"))
            await QueueEverythingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
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

        // Running jobs see the cancelled token, record their failure and finish.
        await dispatcher.WaitForIdleAsync();
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
                        .Where(c => c.Status == ConnectionStatus.Active && c.AdapterKind != AdapterKind.Accounting)
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
