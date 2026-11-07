namespace Synect.Application.Abstractions;

/// <summary>The tenant the current unit of work operates on.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>Scoped, mutable tenant context. Set once per request / per worker scope.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId)
    {
        if (TenantId is { } current && current != tenantId)
            throw new InvalidOperationException("Tenant context is already set to a different tenant for this scope.");
        TenantId = tenantId;
    }
}
