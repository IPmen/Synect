using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Domain.Access;

namespace Synect.DevTools;

/// <summary>create-api-key / revoke-api-key / list-api-keys. The key is printed once and never stored.</summary>
public static class ApiKeyCommands
{
    public static async Task<int> CreateAsync(IServiceProvider services, Guid tenantId, CliArgs options)
    {
        var scopes = (options.Get("scopes") ?? ApiKey.ScopeRead)
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = options.Get("name") ?? $"key {DateTime.UtcNow:yyyy-MM-dd HH:mm}";

        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        var created = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(name, scopes, CancellationToken.None);

        Console.WriteLine($"API key created for tenant {tenantId}");
        Console.WriteLine($"  id:     {created.Id}");
        Console.WriteLine($"  name:   {created.Name}");
        Console.WriteLine($"  prefix: {created.KeyPrefix}");
        Console.WriteLine($"  scopes: {string.Join(", ", created.Scopes)}");
        Console.WriteLine();
        Console.WriteLine("Key (shown only now; Synect stores only its SHA-256 hash):");
        Console.WriteLine(created.Key);
        return 0;
    }

    public static async Task<int> RevokeAsync(IServiceProvider services, Guid tenantId, string idOrPrefix)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        var revoked = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().RevokeAsync(idOrPrefix, CancellationToken.None);
        Console.WriteLine(revoked ? $"Key {idOrPrefix} revoked." : $"No single key '{idOrPrefix}' found for tenant {tenantId}.");
        return revoked ? 0 : 1;
    }

    public static async Task<int> ListAsync(IServiceProvider services, Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        var keys = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().ListAsync(CancellationToken.None);
        foreach (var k in keys)
            Console.WriteLine($"{k.Id}  {k.KeyPrefix}…  [{string.Join(",", k.Scopes)}]  {k.Name}  created {k.CreatedAt:u}" +
                              $"  last used {(k.LastUsedAt is { } u ? u.ToString("u") : "never")}{(k.RevokedAt is { } r ? $"  REVOKED {r:u}" : "")}");
        if (keys.Count == 0) Console.WriteLine("No keys.");
        return 0;
    }
}
