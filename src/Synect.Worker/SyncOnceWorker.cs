using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Sync;
using Synect.Domain.Sync;
using Synect.Infrastructure.Persistence;

namespace Synect.Worker;

/// <summary>
/// Manual trigger: when Sync:RunOnStartup is true, syncs every active connection of every tenant once
/// and stops the host. Scheduling and queueing are intentionally not implemented at this stage.
/// </summary>
public sealed class SyncOnceWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<SyncOnceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Sync:RunOnStartup"))
        {
            logger.LogInformation("Sync:RunOnStartup is false; nothing to do. Scheduling is not implemented yet.");
            lifetime.StopApplication();
            return;
        }

        List<Guid> tenantIds;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SynectDbContext>();
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenantIds)
        {
            // One DI scope per tenant: the tenant context (and RLS setting) never crosses tenants.
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<ConnectionSyncService>();

            var connectionIds = await db.InTransactionAsync(ct => db.Connections
                .Where(c => c.Status != ConnectionStatus.Disabled && c.AdapterKind != AdapterKind.Accounting)
                .Select(c => c.Id)
                .ToListAsync(ct), stoppingToken);

            foreach (var connectionId in connectionIds)
            {
                var outcome = await sync.SyncAsync(connectionId, stoppingToken);
                logger.LogInformation("Connection {ConnectionId}: {Result}, records {Records}, complete {Complete}",
                    connectionId, outcome.Result, outcome.RecordCount, outcome.IsComplete);
            }
        }

        lifetime.StopApplication();
    }
}
