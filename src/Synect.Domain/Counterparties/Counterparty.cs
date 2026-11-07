using Synect.Domain.Common;

namespace Synect.Domain.Counterparties;

/// <summary>A business partner of a tenant, identified by INN.</summary>
public class Counterparty : TenantEntity
{
    public string Inn { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
