using Synect.Domain.Common;

namespace Synect.Domain.Edo;

/// <summary>Append-only history of observed EDO document statuses.</summary>
public class EdoDocumentStatusLog : TenantEntity
{
    public Guid EdoDocumentId { get; init; }
    public string Status { get; init; } = string.Empty;

    /// <summary>When Synect observed the status.</summary>
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>The source "updated" timestamp at the time of observation.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; init; }

    public Guid? SyncRunId { get; init; }
}
