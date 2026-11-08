using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Analytics.Banking;
using Synect.Application.Analytics.Counterparties;

namespace Synect.Application.Analytics;

public static class AnalyticsServiceCollection
{
    /// <summary>Read-only analytics over the Synect database. No dependency on adapters or external systems.</summary>
    public static IServiceCollection AddSynectAnalytics(this IServiceCollection services)
    {
        services.AddScoped<AnalyticsContext>();
        services.AddScoped<BalancesAnalytics>();
        services.AddScoped<FlowsAnalytics>();
        services.AddScoped<IntegrityAnalytics>();
        services.AddScoped<CounterpartyAnalytics>();
        services.AddScoped<DocumentsAnalytics>();
        return services;
    }
}
