using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;
using Synect.Infrastructure;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Persistence;

namespace Synect.Tests.Infrastructure;

public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Wires the real application services, fake adapters and a database.
/// SQLite in-memory is used for fast logic tests (tenant isolation via EF filters);
/// PostgreSQL is used for migrations and row-level security tests.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly IDisposable? _keepAlive;
    private readonly Func<Task>? _cleanup;

    public ServiceProvider Services { get; }
    public TestTimeProvider Time { get; }

    private TestHost(ServiceProvider services, TestTimeProvider time, IDisposable? keepAlive, Func<Task>? cleanup)
    {
        Services = services;
        Time = time;
        _keepAlive = keepAlive;
        _cleanup = cleanup;
    }

    public FakeBankDataSource FakeBank => Services.GetRequiredService<FakeBankDataSource>();
    public FakeEdoDataSource FakeEdo => Services.GetRequiredService<FakeEdoDataSource>();

    public static async Task<TestHost> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var host = Build(o => o.UseSqlite(connection).UseSnakeCaseNamingConvention(), connection, null);
        await using var scope = host.Scope(null);
        await scope.ServiceProvider.GetRequiredService<SynectDbContext>().Database.EnsureCreatedAsync();
        return host;
    }

    /// <param name="appConnectionString">Connection string the application uses (may run as a restricted role).</param>
    public static TestHost CreatePostgres(string appConnectionString, Func<Task> cleanup) =>
        Build(o => o.UseSynectNpgsql(appConnectionString).AddInterceptors(new TenantSessionInterceptor()), null, cleanup);

    private static TestHost Build(Action<DbContextOptionsBuilder> configureDb, IDisposable? keepAlive, Func<Task>? cleanup)
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSynectApplication();
        services.AddSynectAdapters();
        services.AddDbContext<SynectDbContext>(configureDb);
        services.AddScoped<ISynectDbContext>(sp => sp.GetRequiredService<SynectDbContext>());
        return new TestHost(services.BuildServiceProvider(validateScopes: true), time, keepAlive, cleanup);
    }

    /// <summary>A DI scope acting as one tenant (or as nobody when <paramref name="tenantId"/> is null).</summary>
    public AsyncServiceScope Scope(Guid? tenantId)
    {
        var scope = Services.CreateAsyncScope();
        if (tenantId is { } id) scope.ServiceProvider.GetRequiredService<TenantContext>().Set(id);
        return scope;
    }

    public async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Scope(tenantId);
        return await work(scope.ServiceProvider);
    }

    public async Task<Guid> CreateTenantAsync(string name)
    {
        await using var scope = Scope(null);
        var db = scope.ServiceProvider.GetRequiredService<SynectDbContext>();
        var tenant = new Tenant { Name = name, CreatedAt = Time.GetUtcNow() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    public Task<Guid> CreateConnectionAsync(Guid tenantId, AdapterKind kind) =>
        InTenantAsync(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var connection = new Connection
            {
                Name = kind == AdapterKind.Bank ? "Kapital Bank (fake)" : "Didox (fake)",
                SourceCode = kind == AdapterKind.Bank ? FakeBankAdapter.Code : FakeEdoAdapter.Code,
                AdapterKind = kind,
                CredentialsRef = "secret-store://test",
                CreatedAt = Time.GetUtcNow(),
            };
            await db.InTransactionAsync(async ct =>
            {
                db.Connections.Add(connection);
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return connection.Id;
        });

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _keepAlive?.Dispose();
        if (_cleanup is not null) await _cleanup();
    }
}
