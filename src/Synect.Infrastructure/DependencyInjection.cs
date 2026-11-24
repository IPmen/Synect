using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Infrastructure.Access;
using Synect.Application.Adapters;
using Synect.Application.Queue;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.Didox;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Adapters.KapitalBank;
using Synect.Infrastructure.Persistence;
using Synect.Infrastructure.Secrets;

namespace Synect.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Synect";

    /// <summary>Providers whose Adapters:{Provider} sections are bound (rate limits, mapper options).</summary>
    public static readonly string[] Providers = [FakeBankAdapter.Provider, FakeEdoAdapter.Provider];

    /// <summary>
    /// Registers persistence (PostgreSQL), secrets (Data Protection), adapters and application services.
    /// The connection string comes from configuration (user-secrets / environment), never from the repository.
    /// </summary>
    public static IServiceCollection AddSynectInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSynectApplication();
        services.AddSynectOptions(configuration);

        services.AddSingleton<TenantSessionInterceptor>();
        services.AddDbContext<SynectDbContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. " +
                    "Set it with `dotnet user-secrets set \"ConnectionStrings:Synect\" \"...\"` or the ConnectionStrings__Synect environment variable.");
            options.UseSynectNpgsql(connectionString)
                .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
        });
        services.AddScoped<ISynectDbContext>(sp => sp.GetRequiredService<SynectDbContext>());
        services.AddScoped<SchemaCompatibility>();
        services.AddScoped<IApiKeyResolver, PostgresApiKeyResolver>();
        services.AddSingleton<IAccountToolAuditWriter, PostgresAccountToolAuditWriter>();
        services.AddScoped<Synect.Application.Ingest.IIngestKeyResolver, PostgresIngestKeyResolver>();

        services.AddSynectDataProtection(configuration);
        services.AddSynectSecretStore();
        services.AddSynectAdapters(configuration);
        return services;
    }

    /// <summary>Binds Sync:* options.</summary>
    public static IServiceCollection AddSynectOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BankSyncOptions>(configuration.GetSection("Sync:Bank"));
        services.Configure<EdoSyncOptions>(configuration.GetSection("Sync:Edo"));
        services.Configure<CircuitBreakerOptions>(configuration.GetSection("Sync:CircuitBreaker"));
        services.Configure<RetryOptions>(configuration.GetSection("Sync:Retry"));
        services.Configure<SyncQueueOptions>(configuration.GetSection("Sync:Queue"));
        services.Configure<ScheduleOptions>(configuration.GetSection("Sync:Schedule"));
        services.Configure<Synect.Application.Analytics.AnalyticsOptions>(configuration.GetSection("Analytics"));
        services.Configure<Synect.Application.Analytics.Counterparties.SearchOptions>(configuration.GetSection("Search"));
        services.Configure<Synect.Application.Reconciliation.ReconciliationOptions>(configuration.GetSection("Reconciliation"));
        services.Configure<Synect.Application.Attention.AttentionOptions>(configuration.GetSection("Attention"));
        services.Configure<Synect.Application.Accounts.AccountsOptions>(configuration.GetSection("Accounts"));
        services.Configure<Synect.Application.Ingest.IngestOptions>(configuration.GetSection("Ingest"));
        services.Configure<Synect.Application.Accounting1C.AccountingOptions>(configuration.GetSection("Accounting"));
        services.Configure<Synect.Application.OData.ODataOptions>(configuration.GetSection("OData"));
        return services;
    }

    /// <summary>
    /// OData:AllowHttpLocalhostInDevelopment takes effect only when the host runs in Development; anywhere else it is
    /// ignored (base_url stays HTTPS on an allowed host, internal addresses stay refused). Called by each host.
    /// </summary>
    public static IServiceCollection AddODataEnvironment(this IServiceCollection services, bool isDevelopment)
    {
        services.PostConfigure<Synect.Application.OData.ODataOptions>(o =>
        {
            if (o.AllowHttpLocalhostInDevelopment && isDevelopment) o.AllowLocalDevelopment();
        });
        return services;
    }

    /// <summary>
    /// Data Protection keys are kept outside the repository: Secrets:KeysPath, by default
    /// %APPDATA%/Synect/DataProtection-Keys. On Windows the key files are additionally encrypted with DPAPI.
    /// Api and Worker share the application name, so either can decrypt secrets written by the other.
    /// </summary>
    public static IServiceCollection AddSynectDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var keysPath = configuration["Secrets:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
            keysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Synect", "DataProtection-Keys");

        var builder = services.AddDataProtection()
            .SetApplicationName("Synect")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        return services;
    }

    /// <summary>Secret store over connection_secrets. Needs an <see cref="IDataProtectionProvider"/>.</summary>
    public static IServiceCollection AddSynectSecretStore(this IServiceCollection services)
    {
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();
        return services;
    }

    /// <summary>Registers mappers, session/token caches and adapters. Only fake adapters exist at this stage.</summary>
    public static IServiceCollection AddSynectAdapters(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<KapitalBankMapperOptions>(configuration.GetSection("Adapters:KapitalBank"));
            services.Configure<KapitalBankApiOptions>(configuration.GetSection("Adapters:KapitalBank:Api"));
            services.Configure<DidoxMapperOptions>(configuration.GetSection("Adapters:Didox"));
            services.Configure<DidoxTokenOptions>(configuration.GetSection("Adapters:Didox:Token"));
            services.Configure<DidoxApiOptions>(configuration.GetSection("Adapters:Didox:Api"));
            foreach (var provider in Providers)
                services.Configure<RateLimitOptions>(provider, configuration.GetSection($"Adapters:{provider}:RateLimit"));
        }
        else
        {
            services.AddOptions<KapitalBankMapperOptions>();
            services.AddOptions<KapitalBankApiOptions>();
            services.AddOptions<DidoxMapperOptions>();
            services.AddOptions<DidoxTokenOptions>();
            services.AddOptions<DidoxApiOptions>();
        }

        services.AddSingleton<KapitalBankMapper>();
        services.AddSingleton<KapitalSessionManager>();
        services.AddSingleton<DidoxMapper>();
        services.AddSingleton<DidoxTokenCache>();
        services.AddSingleton<FakeBankDataSource>();
        services.AddSingleton<FakeEdoDataSource>();
        services.AddSingleton<IBankAdapter, FakeBankAdapter>();
        // The real Kapital Bank adapter (source code "kapitalbank"): used only by connections created with that source code.
        services.AddSingleton(_ => KapitalBankHttp.CreateDefault());
        services.AddSingleton<IBankAdapter, KapitalBankAdapter>();
        services.AddSingleton<IEdoAdapter, FakeEdoAdapter>();
        // The real Didox adapter (source code "didox"): used only by connections created with that source code.
        services.AddSingleton(_ => DidoxHttp.CreateDefault());
        services.AddSingleton<IEdoAdapter, DidoxEdoAdapter>();
        // 1C:Fresh over OData (source code "1c_fresh_odata"): read-only client, transport with SSRF protection.
        services.AddSingleton(sp => Adapters.OData.ODataTransport.CreateDefault(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Synect.Application.OData.ODataOptions>>()));
        services.AddSingleton<Synect.Application.OData.IODataClientFactory, Adapters.OData.ODataHttpClientFactory>();
        services.AddScoped<Adapters.OData.ODataConnectionService>();
        services.AddScoped<ConnectionCredentialsService>();
        return services;
    }

    public static DbContextOptionsBuilder UseSynectNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    public static DbContextOptionsBuilder<T> UseSynectNpgsql<T>(this DbContextOptionsBuilder<T> options, string connectionString)
        where T : DbContext =>
        (DbContextOptionsBuilder<T>)((DbContextOptionsBuilder)options).UseSynectNpgsql(connectionString);
}
