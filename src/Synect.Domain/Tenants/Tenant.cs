namespace Synect.Domain.Tenants;

/// <summary>A client of the platform. Root of the tenant isolation boundary.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Taxpayer identification number (INN / STIR) of the organization: 9 or 14 digits, optional, unique within an account
    /// (column tin, stage 6.5).
    /// </summary>
    public string? Tin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
