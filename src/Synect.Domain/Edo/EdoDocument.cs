using Synect.Domain.Common;

namespace Synect.Domain.Edo;

public enum EdoDocumentOwner
{
    /// <summary>Received from a counterparty (Didox owner = 0).</summary>
    Incoming = 0,

    /// <summary>Sent by the tenant (Didox owner = 1).</summary>
    Outgoing = 1,
}

/// <summary>An electronic document from an EDO provider (Didox). Amounts are in minor units.</summary>
public class EdoDocument : TenantEntity
{
    public Guid ConnectionId { get; set; }
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Source document type code, e.g. "002".</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>Source status code as received, e.g. "3". Normalization is pending confirmation.</summary>
    public string Status { get; private set; } = string.Empty;

    public EdoDocumentOwner Owner { get; set; }
    public string? Number { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public string? CounterpartyInn { get; set; }
    public string? CounterpartyName { get; set; }
    public string? ContractNumber { get; set; }
    public DateOnly? ContractDate { get; set; }
    public long TotalSum { get; set; }
    public long TotalVatSum { get; set; }
    public long TotalDeliverySum { get; set; }
    public long TotalDeliverySumWithVat { get; set; }

    /// <summary>Creation time reported by the source.</summary>
    public DateTimeOffset? SourceCreatedAt { get; set; }

    /// <summary>Last update time reported by the source.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    public string SourceHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Sets the status and returns a log entry when it actually changed (or was set for the first time).
    /// </summary>
    public EdoDocumentStatusLog? ApplyStatus(string status, DateTimeOffset recordedAt, Guid? syncRunId)
    {
        if (Status == status) return null;
        Status = status;
        return new EdoDocumentStatusLog
        {
            TenantId = TenantId,
            EdoDocumentId = Id,
            Status = status,
            RecordedAt = recordedAt,
            SourceUpdatedAt = SourceUpdatedAt,
            SyncRunId = syncRunId,
        };
    }
}
