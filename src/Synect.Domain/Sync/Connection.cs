using Synect.Domain.Common;

namespace Synect.Domain.Sync;

public enum AdapterKind
{
    Bank = 1,
    Edo = 2,

    /// <summary>
    /// 1C accounting (stage 8a): the 1C extension pushes packages to /ingest/1c/v1/batches with an ingest key. Never planned or
    /// queued; <see cref="Connection.LastSuccessfulSyncAt"/> is the time of the last accepted package.
    /// </summary>
    Accounting1C = 3,

    /// <summary>
    /// Bank statements imported from files (MT940, CSV) for banks without an API. Never planned or queued: data arrives
    /// only when a file is imported; freshness is the time of the last import.
    /// </summary>
    File = 4,
}

public enum ConnectionStatus
{
    Active = 1,
    NeedsAttention = 2,
    Disabled = 3,
}

/// <summary>
/// Manual onboarding step of a connection (stage 6.5), e.g. "the application to the bank is submitted". It is only a mark
/// for people and never changes how the connection is loaded (<see cref="Connection.Status"/>).
/// </summary>
public enum OnboardingStatus
{
    NotStarted = 0,
    WaitingForAccess = 1,
    Connected = 2,
    NeedsAttention = 3,
}

/// <summary>
/// A configured link between a tenant and one external source (a bank, Didox, 1C).
/// Credentials are never stored here; <see cref="CredentialsRef"/> points to a row in connection_secrets.
/// </summary>
public class Connection : TenantEntity
{
    /// <summary>Source code of file import connections (<see cref="AdapterKind.File"/>); no adapter is registered for it.</summary>
    public const string FileImportSourceCode = "file_import";

    /// <summary>Source code of the 1C connection (<see cref="AdapterKind.Accounting1C"/>); no adapter is registered for it.</summary>
    public const string Accounting1CSourceCode = "1c";

    /// <summary>True for connections whose data is loaded by the sync queue (not file imports or 1C, whose data is pushed).</summary>
    public bool IsSyncable => AdapterKind is AdapterKind.Bank or AdapterKind.Edo;

    public string Name { get; set; } = string.Empty;

    /// <summary>Adapter registry key, e.g. "kapitalbank", "didox", "fake_kapitalbank"; "file_import" for file connections.</summary>
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

    public const int MaxOnboardingNoteLength = 500;

    /// <summary>Manual onboarding mark; does not affect loading.</summary>
    public OnboardingStatus OnboardingStatus { get; private set; } = OnboardingStatus.NotStarted;

    /// <summary>Optional note to the onboarding mark (at most 500 characters).</summary>
    public string? OnboardingNote { get; private set; }

    public void SetOnboarding(OnboardingStatus status, string? note)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxOnboardingNoteLength })
            throw new ArgumentException($"The onboarding note is longer than {MaxOnboardingNoteLength} characters.", nameof(note));
        OnboardingStatus = status;
        OnboardingNote = note;
    }

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
