using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Sync;
using Synect.Infrastructure.Adapters.Didox;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Adapters.KapitalBank;
using Synect.Infrastructure.Persistence;

namespace Synect.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Synect";

    /// <summary>
    /// Registers persistence (PostgreSQL), adapters and application services.
    /// The connection string comes from configuration (user-secrets / environment), never from the repository.
    /// </summary>
    public static IServiceCollection AddSynectInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSynectApplication();
        services.Configure<BankSyncOptions>(configuration.GetSection("Sync:Bank"));
        services.Configure<EdoSyncOptions>(configuration.GetSection("Sync:Edo"));

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

        services.AddSynectAdapters(configuration);
        return services;
    }

    /// <summary>Registers mappers and adapters. Only fake adapters exist at this stage.</summary>
    public static IServiceCollection AddSynectAdapters(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<KapitalBankMapperOptions>(configuration.GetSection("Adapters:KapitalBank"));
            services.Configure<DidoxMapperOptions>(configuration.GetSection("Adapters:Didox"));
        }
        else
        {
            services.AddOptions<KapitalBankMapperOptions>();
            services.AddOptions<DidoxMapperOptions>();
        }

        services.AddSingleton<KapitalBankMapper>();
        services.AddSingleton<DidoxMapper>();
        services.AddSingleton<FakeBankDataSource>();
        services.AddSingleton<FakeEdoDataSource>();
        services.AddSingleton<IBankAdapter, FakeBankAdapter>();
        services.AddSingleton<IEdoAdapter, FakeEdoAdapter>();
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
