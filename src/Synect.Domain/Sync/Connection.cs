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

    /// <summary>
    /// Source code of the 1C:Fresh (Clobus) connection read over the standard OData interface (<see cref="AdapterKind.Accounting1C"/>,
    /// pull): Synect itself loads the data on the schedule and writes it through the same receiver as the 1C extension.
    /// </summary>
    public const string OneCFreshODataSourceCode = "1c_fresh_odata";

    /// <summary>True for connections whose data is loaded by the sync queue (banks, Didox, 1C over OData), not pushed or imported.</summary>
    public bool IsSyncable => Transport == ConnectionKinds.TransportPull;

    /// <summary>A 1C connection of either kind (extension or OData). An organization has at most one that is not Disabled.</summary>
    public bool IsOneC => AdapterKind == AdapterKind.Accounting1C;

    /// <summary>The 1C:Fresh OData connection (pull).</summary>
    public bool IsOneCOData => AdapterKind == AdapterKind.Accounting1C && SourceCode == OneCFreshODataSourceCode;

    /// <summary>Kind of the connection (<see cref="ConnectionKinds"/>), derived from the adapter kind and the source code.</summary>
    public string Kind => ConnectionKinds.Of(AdapterKind, SourceCode);

    /// <summary>push (data is sent to Synect: 1C extension, file import) or pull (Synect loads it); derived from <see cref="Kind"/>.</summary>
    public string Transport => ConnectionKinds.TransportOf(Kind);

    /// <summary>
    /// Where the 1C application runs (1C connections only): desktop, clobus or other. For OData it comes from the host of
    /// base_url (stored in <see cref="Settings"/> as host_label when the connection is created); the extension is desktop
    /// unless host_label says otherwise. Null for non-1C connections.
    /// </summary>
    public string? HostLabel => !IsOneC ? null : ConnectionKinds.ReadSetting(Settings, "host_label") ?? (IsOneCOData ? ConnectionKinds.HostOther : ConnectionKinds.HostDesktop);

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

/// <summary>Connection kinds, transports and host labels as shown to people and tools.</summary>
public static class ConnectionKinds
{
    public const string OneCExtension = "onec_extension";
    public const string OneCFreshOData = "onec_fresh_odata";
    public const string Didox = "didox";
    public const string KapitalBank = "kapital_bank";
    public const string FileImport = "file_import";
    public const string Bank = "bank";
    public const string Edo = "edo";

    public const string TransportPush = "push";
    public const string TransportPull = "pull";

    public const string HostDesktop = "desktop";
    public const string HostClobus = "clobus";
    public const string HostOther = "other";

    public static string Of(AdapterKind adapterKind, string sourceCode) => adapterKind switch
    {
        AdapterKind.Accounting1C => sourceCode == Connection.OneCFreshODataSourceCode ? OneCFreshOData : OneCExtension,
        AdapterKind.File => FileImport,
        AdapterKind.Bank => sourceCode.Contains("kapital", StringComparison.OrdinalIgnoreCase) ? KapitalBank : Bank,
        AdapterKind.Edo => sourceCode.Contains("didox", StringComparison.OrdinalIgnoreCase) ? Didox : Edo,
        _ => adapterKind.ToString().ToLowerInvariant(),
    };

    /// <summary>The 1C extension and file imports push data to Synect; everything else is loaded by Synect.</summary>
    public static string TransportOf(string kind) => kind is OneCExtension or FileImport ? TransportPush : TransportPull;

    /// <summary>A string property of the settings JSON object, or null (malformed settings count as empty).</summary>
    public static string? ReadSetting(string? settingsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                   && v.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
