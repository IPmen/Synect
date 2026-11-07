using Synect.Domain.Common;

namespace Synect.Domain.Sync;

public enum AdapterKind
{
    Bank = 1,
    Edo = 2,
    Accounting = 3,
}

public enum ConnectionStatus
{
    Active = 1,
    NeedsAttention = 2,
    Disabled = 3,
}

/// <summary>
/// A configured link between a tenant and one external source (a bank, Didox, 1C).
/// Credentials are never stored here; <see cref="CredentialsRef"/> points to a secret store entry.
/// </summary>
public class Connection : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Adapter registry key, e.g. "kapitalbank", "didox", "fake_kapitalbank".</summary>
    public string SourceCode { get; set; } = string.Empty;

    public AdapterKind AdapterKind { get; set; }
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Active;
    public string? StatusReason { get; private set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; private set; }

    /// <summary>Non-secret adapter settings as a JSON object (stored as jsonb).</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Key of the credentials in the secret store. Never the secret itself.</summary>
    public string? CredentialsRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public void MarkSyncSucceeded(DateTimeOffset at)
    {
        LastSuccessfulSyncAt = at;
        if (Status == ConnectionStatus.NeedsAttention)
        {
            Status = ConnectionStatus.Active;
            StatusReason = null;
        }
    }

    public void MarkNeedsAttention(string reason)
    {
        if (Status == ConnectionStatus.Disabled) return;
        Status = ConnectionStatus.NeedsAttention;
        StatusReason = reason;
    }

    public void Disable(string? reason = null)
    {
        Status = ConnectionStatus.Disabled;
        StatusReason = reason;
    }

    public void Activate()
    {
        Status = ConnectionStatus.Active;
        StatusReason = null;
    }
}
