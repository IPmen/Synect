using Synect.Domain.Common;

namespace Synect.Domain.Banking;

public class BankAccount : TenantEntity
{
    public Guid ConnectionId { get; set; }

    /// <summary>20-digit account number.</summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>Bank branch code (MFO).</summary>
    public string Mfo { get; set; } = string.Empty;

    /// <summary>ISO 4217 alphabetic code, e.g. "UZS".</summary>
    public string Currency { get; set; } = string.Empty;

    public string? Name { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
