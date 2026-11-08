using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Sync;
using Synect.Domain.Counterparties;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Isolation;

/// <summary>Application-level isolation (EF query filters + SaveChanges checks). RLS is covered in PostgresTests.</summary>
public class TenantIsolationTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenantA, _tenantB, _bankA, _bankB, _edoA, _edoB;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenantA = await _host.CreateTenantAsync("Tenant A");
        _tenantB = await _host.CreateTenantAsync("Tenant B");
        _bankA = await _host.CreateConnectionAsync(_tenantA, AdapterKind.Bank);
        _bankB = await _host.CreateConnectionAsync(_tenantB, AdapterKind.Bank);
        _edoA = await _host.CreateConnectionAsync(_tenantA, AdapterKind.Edo);
        _edoB = await _host.CreateConnectionAsync(_tenantB, AdapterKind.Edo);
        // Same fake seeds for both tenants: identical external ids must not collide across tenants.
        await _host.SyncBankAsync(_tenantA, _bankA);
        await _host.SyncBankAsync(_tenantB, _bankB);
        await _host.SyncEdoAsync(_tenantA, _edoA);
        await _host.SyncEdoAsync(_tenantB, _edoB);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Each_tenant_sees_only_its_own_rows_in_every_table()
    {
        foreach (var (tenant, other) in new[] { (_tenantA, _tenantB), (_tenantB, _tenantA) })
        {
            var tenantIds = await _host.QueryAsync(tenant, async db => new[]
            {
                await db.Connections.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.SyncRuns.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.RawObjects.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.BankAccounts.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.BankTransactions.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.BankDailyBalances.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.EdoDocuments.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.EdoDocumentStatusLogs.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.Counterparties.Select(x => x.TenantId).Distinct().ToListAsync(),
                await db.ConnectionSecrets.Select(x => x.TenantId).Distinct().ToListAsync(),
            });
            Assert.All(tenantIds, ids => Assert.Equal([tenant], ids));
        }
    }

    [Fact]
    public async Task Lookup_by_id_of_another_tenant_returns_nothing()
    {
        var txOfB = await _host.QueryAsync(_tenantB, db => db.BankTransactions.Select(t => t.Id).FirstAsync());

        Assert.Null(await _host.QueryAsync(_tenantA, db => db.BankTransactions.SingleOrDefaultAsync(t => t.Id == txOfB)));
        Assert.Null(await _host.QueryAsync(_tenantA, db => db.Connections.SingleOrDefaultAsync(c => c.Id == _bankB)));
    }

    [Fact]
    public async Task Without_tenant_context_nothing_is_visible()
    {
        Assert.Equal(0, await _host.CountAsync(null, db => db.BankTransactions));
        Assert.Equal(0, await _host.CountAsync(null, db => db.EdoDocuments));
        Assert.Equal(0, await _host.CountAsync(null, db => db.Connections));
    }

    [Fact]
    public async Task Tenant_cannot_sync_a_connection_of_another_tenant()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _host.InTenantAsync(_tenantA,
            sp => sp.GetRequiredService<ConnectionSyncService>().SyncAsync(_bankB, CancellationToken.None)));
    }

    [Fact]
    public async Task Writing_a_row_for_another_tenant_is_rejected()
    {
        await using var scope = _host.Scope(_tenantA);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        db.Counterparties.Add(new Counterparty { TenantId = _tenantB, Inn = "399999999", CreatedAt = _host.Time.Now, UpdatedAt = _host.Time.Now });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
