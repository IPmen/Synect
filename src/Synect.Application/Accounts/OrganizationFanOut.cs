using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;

namespace Synect.Application.Accounts;

/// <summary>Accounts section (stage 6.5).</summary>
public sealed class AccountsOptions
{
    /// <summary>Organizations processed at the same time by one multi-organization operation (each in its own scope).</summary>
    public int MaxParallelOrganizations { get; set; } = 4;

    /// <summary>Unmatched payments in the portfolio overview are counted over this many recent days.</summary>
    public int UnmatchedLookbackDays { get; set; } = 90;

    /// <summary>Trigram similarity (0..1) from which an organization name is a candidate for a name given in a question.</summary>
    public double MinNameSimilarity { get; set; } = 0.45;
}

/// <summary>Result of one organization of a multi-organization operation: a value, or the error of that organization only.</summary>
public sealed record OrganizationOutcome<T>(Guid TenantId, T? Value, Exception? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Runs work for several organizations (tenants) without ever mixing them: each organization gets its own DI scope, hence
/// its own DbContext, connection use, transaction and SET LOCAL app.tenant_id (TenantSessionInterceptor) and its own
/// tenant context; there is never a context or transaction that sees two tenants. At most
/// Accounts:MaxParallelOrganizations organizations run at the same time. An error of one organization is recorded for it
/// alone; the others still run, and no value of a failed organization is returned. Cancellation of the caller stops all.
/// </summary>
public sealed class OrganizationFanOut(IServiceScopeFactory scopes, IOptions<AccountsOptions> options)
{
    public async Task<IReadOnlyList<OrganizationOutcome<T>>> RunAsync<T>(
        IReadOnlyList<Guid> tenants, Func<IServiceProvider, Guid, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (tenants.Distinct().Count() != tenants.Count) throw new ArgumentException("Each organization may appear only once.", nameof(tenants));
        var results = new OrganizationOutcome<T>[tenants.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelOrganizations));
        await Task.WhenAll(tenants.Select(async (tenant, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await RunOneAsync(tenant, work, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results;
    }

    private async Task<OrganizationOutcome<T>> RunOneAsync<T>(Guid tenant, Func<IServiceProvider, Guid, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
            var value = await work(scope.ServiceProvider, tenant, ct);
            return new OrganizationOutcome<T>(tenant, value, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new OrganizationOutcome<T>(tenant, default, ex);
        }
    }
}
