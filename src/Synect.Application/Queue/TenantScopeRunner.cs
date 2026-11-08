using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;

namespace Synect.Application.Queue;

/// <summary>
/// Runs work on behalf of one tenant in its own DI scope with the tenant context set, so that every
/// transaction gets SET LOCAL app.tenant_id and row-level security applies. Tenants are read from the
/// tenants table, which is not under RLS.
/// </summary>
public sealed class TenantScopeRunner(IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        return await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
    }

    public async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        return await work(scope.ServiceProvider, ct);
    }
}
