using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Sync;
using Synect.Domain.Sync;

namespace Synect.Tests.Infrastructure;

internal static class TestData
{
    public static readonly DateOnly Day1 = new(2026, 11, 2);
    public static readonly DateOnly Day5 = new(2026, 11, 6);

    public static Task<SyncOutcome> SyncBankAsync(this TestHost host, Guid tenantId, Guid connectionId, DateOnly? from = null, DateOnly? to = null) =>
        host.InTenantAsync(tenantId, sp => sp.GetRequiredService<BankSyncService>()
            .SyncAsync(connectionId, from ?? Day1, to ?? Day5, CancellationToken.None));

    /// <summary>Incremental bank load (range decided by the service).</summary>
    public static Task<SyncOutcome> SyncBankIncrementalAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.InTenantAsync(tenantId, sp => sp.GetRequiredService<BankSyncService>().SyncAsync(connectionId, CancellationToken.None));

    public static Task<SyncRun> LastRunAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.QueryAsync(tenantId, db => db.SyncRuns.AsNoTracking()
            .Where(r => r.ConnectionId == connectionId).OrderByDescending(r => r.StartedAt).FirstAsync());

    public static Task<Connection> ConnectionAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.QueryAsync(tenantId, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == connectionId));

    public static Task<List<SyncJob>> JobsAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.QueryAsync(tenantId, db => db.SyncJobs.AsNoTracking()
            .Where(j => j.ConnectionId == connectionId).OrderBy(j => j.EnqueuedAt).ToListAsync());

    public static Task<SyncRequestResult> RequestSyncAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.InTenantAsync(tenantId, sp => sp.GetRequiredService<SyncRequestService>().RequestSyncAsync(connectionId, CancellationToken.None));

    /// <summary>One dispatcher tick, then waits until every started job has finished.</summary>
    public static async Task<int> RunQueueAsync(this TestHost host)
    {
        var started = await host.Dispatcher.TickAsync(CancellationToken.None);
        await host.Dispatcher.WaitForIdleAsync();
        return started;
    }

    public static Task<SyncOutcome> SyncEdoAsync(this TestHost host, Guid tenantId, Guid connectionId) =>
        host.InTenantAsync(tenantId, sp => sp.GetRequiredService<EdoSyncService>().SyncAsync(connectionId, CancellationToken.None));

    /// <summary>Runs a read inside a transaction, as application code must do under RLS.</summary>
    public static Task<T> QueryAsync<T>(this TestHost host, Guid? tenantId, Func<ISynectDbContext, Task<T>> query) =>
        Run(host, tenantId, query);

    private static async Task<T> Run<T>(TestHost host, Guid? tenantId, Func<ISynectDbContext, Task<T>> query)
    {
        await using var scope = host.Scope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        return await db.InTransactionAsync(_ => query(db), CancellationToken.None);
    }

    public static Task<int> CountAsync<T>(this TestHost host, Guid? tenantId, Func<ISynectDbContext, IQueryable<T>> set) =>
        host.QueryAsync(tenantId, db => set(db).CountAsync());
}
