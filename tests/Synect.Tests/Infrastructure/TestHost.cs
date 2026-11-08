using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synect.Application;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;
using Synect.Infrastructure;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Persistence;

namespace Synect.Tests.Infrastructure;

public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;
    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}

/// <summary>Collects every log line (message, structured values and exception) for leak checks.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}")) : "";
            owner.Lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}

/// <summary>SQLite cannot compare or order DateTimeOffset; tests store it as UTC ticks.</summary>
public sealed class SqliteTestModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    private static readonly ValueConverter<DateTimeOffset, long> Ticks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(Ticks);
        }
    }
}

/// <summary>
/// Wires the real application services, fake adapters, an ephemeral Data Protection key ring and a database.
/// SQLite (a temporary WAL file, so that parallel jobs work) is used for logic tests;
/// PostgreSQL is used for migrations, row-level security and end-to-end checks.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly Func<Task>? _cleanup;

    public ServiceProvider Services { get; }
    public TestTimeProvider Time { get; }
    public CapturingLoggerProvider Logs { get; }

    /// <summary>Plain passwords generated for test connections (randomly, never written in test source).</summary>
    public ConcurrentDictionary<Guid, string> Passwords { get; } = new();

    private TestHost(ServiceProvider services, TestTimeProvider time, CapturingLoggerProvider logs, Func<Task>? cleanup)
    {
        Services = services;
        Time = time;
        Logs = logs;
        _cleanup = cleanup;
    }

    public FakeBankDataSource FakeBank => Services.GetRequiredService<FakeBankDataSource>();
    public FakeEdoDataSource FakeEdo => Services.GetRequiredService<FakeEdoDataSource>();
    public SyncDispatcher Dispatcher => Services.GetRequiredService<SyncDispatcher>();

    public static async Task<TestHost> CreateSqliteAsync(Action<IServiceCollection>? configure = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synect-test-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        var host = Build(
            o => o.UseSqlite(cs).UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>(),
            configure,
            () =>
            {
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                    try { File.Delete(file); } catch (IOException) { }
                return Task.CompletedTask;
            });
        host.SqliteConnectionStringValue = cs;

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync();
        }
        await using var scope = host.Scope(null);
        await scope.ServiceProvider.GetRequiredService<SynectDbContext>().Database.EnsureCreatedAsync();
        return host;
    }

    /// <summary>Connection string of the SQLite test database (null for PostgreSQL hosts).</summary>
    public string? SqliteConnectionStringValue { get; private set; }

    /// <param name="appConnectionString">Connection string the application uses (may run as a restricted role).</param>
    public static TestHost CreatePostgres(string appConnectionString, Func<Task> cleanup, Action<IServiceCollection>? configure = null) =>
        Build(o => o.UseSynectNpgsql(appConnectionString).AddInterceptors(new TenantSessionInterceptor()), configure, cleanup);

    private static TestHost Build(Action<DbContextOptionsBuilder> configureDb, Action<IServiceCollection>? configure, Func<Task>? cleanup)
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(time);
        services.AddSynectApplication();
        services.AddSynectAdapters();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSynectSecretStore();
        services.AddDbContext<SynectDbContext>(configureDb);
        services.AddScoped<ISynectDbContext>(sp => sp.GetRequiredService<SynectDbContext>());

        // Fast and unthrottled by default; individual tests tighten these.
        services.Configure<RetryOptions>(o => { o.BaseDelay = TimeSpan.FromMilliseconds(1); o.MaxDelay = TimeSpan.FromMilliseconds(5); });
        services.ConfigureAll<RateLimitOptions>(o =>
        {
            o.PerConnection = new TokenBucketSettings { TokenLimit = 100_000, TokensPerPeriod = 100_000 };
            o.PerSource = new TokenBucketSettings { TokenLimit = 100_000, TokensPerPeriod = 100_000 };
        });
        services.Configure<SyncQueueOptions>(o => o.JobTimeout = TimeSpan.FromSeconds(30));
        configure?.Invoke(services);

        return new TestHost(services.BuildServiceProvider(validateScopes: true), time, logs, cleanup);
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

    /// <summary>Creates a connection with randomly generated credentials stored in the secret store.</summary>
    public Task<Guid> CreateConnectionAsync(Guid tenantId, AdapterKind kind) =>
        InTenantAsync(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var connection = new Connection
            {
                Name = (kind == AdapterKind.Bank ? "Kapital Bank (fake) " : "Didox (fake) ") + Guid.NewGuid().ToString("N")[..6],
                SourceCode = kind == AdapterKind.Bank ? FakeBankAdapter.Code : FakeEdoAdapter.Code,
                AdapterKind = kind,
                CreatedAt = Time.GetUtcNow(),
            };
            await db.InTransactionAsync(async ct =>
            {
                db.Connections.Add(connection);
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);

            var password = RandomSecret();
            Passwords[connection.Id] = password;
            await sp.GetRequiredService<ISecretStore>().SetAsync(connection.Id, ConnectionSecret.CredentialsKind,
                LoginPasswordCredentials.Create("user-" + connection.Id.ToString("N")[..8], password), CancellationToken.None);
            return connection.Id;
        });

    public static string RandomSecret() => "pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public async ValueTask DisposeAsync()
    {
        await Dispatcher.WaitForIdleAsync();
        await Services.DisposeAsync();
        if (_cleanup is not null) await _cleanup();
    }
}
