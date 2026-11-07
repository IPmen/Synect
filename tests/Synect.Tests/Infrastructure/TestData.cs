using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Sync;

namespace Synect.Tests.Infrastructure;

internal static class TestData
{
    public static readonly DateOnly Day1 = new(2026, 11, 2);
    public static readonly DateOnly Day5 = new(2026, 11, 6);

    public static Task<SyncOutcome> SyncBankAsync(this TestHost host, Guid tenantId, Guid connectionId, DateOnly? from = null, DateOnly? to = null) =>
        host.InTenantAsync(tenantId, sp => sp.GetRequiredService<BankSyncService>()
            .SyncAsync(connectionId, from ?? Day1, to ?? Day5, CancellationToken.None));

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
