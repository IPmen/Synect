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
/// Credentials are never stored here; <see cref="CredentialsRef"/> points to a row in connection_secrets.
/// </summary>
public class Connection : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Adapter registry key, e.g. "kapitalbank", "didox", "fake_kapitalbank".</summary>
    public string SourceCode { get; set; } = string.Empty;

    public AdapterKind AdapterKind { get; set; }
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Active;

    /// <summary>Why the connection is in its current state (null when Active).</summary>
    public string? StatusReason { get; private set; }

    /// <summary>When <see cref="Status"/> last changed; null if it never changed since creation.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    public DateTimeOffset? LastSuccessfulSyncAt { get; private set; }

    /// <summary>Failed loads in a row since the last successful one (circuit breaker input).</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Non-secret adapter settings as a JSON object (stored as jsonb).</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Reference to the credentials row in connection_secrets ("connection_secrets:{id}"). Never the secret itself.</summary>
    public string? CredentialsRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public void MarkSyncSucceeded(DateTimeOffset at)
    {
        LastSuccessfulSyncAt = at;
        ConsecutiveFailures = 0;
        if (Status == ConnectionStatus.NeedsAttention)
            ChangeStatus(ConnectionStatus.Active, null, at);
    }

    /// <summary>
    /// Records a failed load. Opens the circuit (NeedsAttention) when the failure is an authentication
    /// problem or when <paramref name="threshold"/> failures happened in a row. Returns true if it opened.
    /// </summary>
    public bool RecordSyncFailure(string reason, bool authenticationFailure, int threshold, DateTimeOffset at)
    {
        ConsecutiveFailures++;
        if (Status != ConnectionStatus.Active) return false;
        if (authenticationFailure)
        {
            ChangeStatus(ConnectionStatus.NeedsAttention, reason, at);
            return true;
        }
        if (ConsecutiveFailures >= threshold)
        {
            ChangeStatus(ConnectionStatus.NeedsAttention, $"{ConsecutiveFailures} failed loads in a row; last error: {reason}", at);
            return true;
        }
        return false;
    }

    public void MarkNeedsAttention(string reason, DateTimeOffset at)
    {
        if (Status == ConnectionStatus.Disabled) return;
        ChangeStatus(ConnectionStatus.NeedsAttention, reason, at);
    }

    public void Disable(string? reason, DateTimeOffset at) => ChangeStatus(ConnectionStatus.Disabled, reason, at);

    /// <summary>Back to Active (e.g. after a successful connection test). Resets the circuit breaker.</summary>
    public void Activate(DateTimeOffset at)
    {
        ConsecutiveFailures = 0;
        ChangeStatus(ConnectionStatus.Active, null, at);
    }

    private void ChangeStatus(ConnectionStatus status, string? reason, DateTimeOffset at)
    {
        if (reason is { Length: > 2000 }) reason = reason[..2000];
        if (Status != status) StatusChangedAt = at;
        Status = status;
        StatusReason = reason;
    }
}
