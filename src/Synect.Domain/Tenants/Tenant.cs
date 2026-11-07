namespace Synect.Domain.Tenants;

/// <summary>A client of the platform. Root of the tenant isolation boundary.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;

    /// <summary>Taxpayer identification number (INN / STIR) of the client company.</summary>
    public string? Inn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
