namespace Synect.Domain.Common;

/// <summary>Marks an entity that belongs to exactly one tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>
/// Base class for tenant-scoped entities. The primary key is (TenantId, Id),
/// so every table and index starts with tenant_id.
/// </summary>
public abstract class TenantEntity : ITenantOwned
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    /// <summary>Assigned by the persistence layer from the current tenant context when left empty.</summary>
    public Guid TenantId { get; set; }
}
