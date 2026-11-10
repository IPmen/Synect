using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Application.Adapters;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Totals;
using Synect.Application.Freshness;
using Synect.Application.Queue;
using Synect.Application.Reconciliation;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Application.Sync;

namespace Synect.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSynectApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddSingleton<IAdapterRegistry, AdapterRegistry>();

        // Options (bound from configuration by the host / infrastructure).
        services.AddOptions<BankSyncOptions>();
        services.AddOptions<EdoSyncOptions>();
        services.AddOptions<CircuitBreakerOptions>();
        services.AddOptions<RetryOptions>();
        services.AddOptions<RateLimitOptions>();
        services.AddOptions<SyncQueueOptions>();
        services.AddOptions<ScheduleOptions>();

        // Resilience: shared across scopes so that buckets survive between jobs.
        services.AddSingleton<AdapterRateLimiters>();
        services.AddSingleton<AdapterCallPolicy>();

        // Loading.
        services.AddScoped<ICredentialProvider, SecretStoreCredentialProvider>();
        services.AddScoped<RawObjectStore>();
        services.AddScoped<CounterpartyStore>();
        services.AddScoped<CounterpartyAliasRebuilder>();
        services.AddScoped<BankSyncService>();
        services.AddScoped<EdoSyncService>();
        services.AddScoped<ConnectionSyncService>();

        // Queue and scheduler.
        services.AddSingleton<SyncScheduleCalculator>();
        services.AddSingleton<TenantScopeRunner>();
        services.AddSingleton<SyncDispatcher>();
        services.AddScoped<SyncJobQueue>();
        services.AddScoped<SyncRequestService>();
        services.AddScoped<SyncPlanner>();
        services.AddScoped<SyncRecovery>();

        services.AddScoped<ISourceFreshnessQuery, SourceFreshnessQuery>();

        // Client API keys and the tool call journal (MCP).
        services.AddScoped<ApiKeyService>();
        services.AddSingleton<ToolAuditWriter>();

        // Analytics (read-only over the database) and precomputed totals.
        services.AddOptions<AnalyticsOptions>();
        services.AddScoped<AnalyticsChangeSet>();
        services.AddScoped<CounterpartyMonthlyRefresher>();
        services.AddSingleton<AliasVolumeMonitor>();

        // Reconciliation of payments with documents (reads and writes only Synect's own tables).
        services.AddOptions<ReconciliationOptions>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<ReconciliationReviewService>();
        services.AddScoped<ReconciliationQueries>();
        services.AddSynectAnalytics();
        return services;
    }
}
