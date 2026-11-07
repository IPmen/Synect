using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
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

        services.AddOptions<BankSyncOptions>();
        services.AddOptions<EdoSyncOptions>();
        services.AddScoped<RawObjectStore>();
        services.AddScoped<CounterpartyStore>();
        services.AddScoped<BankSyncService>();
        services.AddScoped<EdoSyncService>();
        services.AddScoped<ConnectionSyncService>();
        return services;
    }
}
