using Synect.Domain.Edo;

namespace Synect.Application.Adapters;

/// <param name="SupportsUpdatedSince">The provider can filter the list by last update time.</param>
/// <param name="PageSize">Number of documents per page.</param>
public sealed record EdoAdapterCapabilities(bool SupportsUpdatedSince, int PageSize);

/// <summary>An EDO document in the common model. Amounts are in minor units.</summary>
public sealed record EdoDocumentInfo(
    string ExternalId,
    string DocType,
    string Status,
    EdoDocumentOwner Owner,
    string? Number,
    DateOnly? DocumentDate,
    string? CounterpartyInn,
    string? CounterpartyName,
    string? ContractNumber,
    DateOnly? ContractDate,
    long TotalSum,
    long TotalVatSum,
    long TotalDeliverySum,
    long TotalDeliverySumWithVat,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    RawRecord Raw);

public sealed record EdoDocumentPage(IReadOnlyList<EdoDocumentInfo> Items, int Page, bool HasMore);

public interface IEdoAdapter : ISourceAdapter
{
    EdoAdapterCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct);

    /// <param name="page">1-based page number.</param>
    Task<EdoDocumentPage> ListDocumentsAsync(
        AdapterContext context,
        EdoDocumentOwner owner,
        DateTimeOffset? updatedSince,
        int page,
        CancellationToken ct);

    Task<EdoDocumentInfo?> GetDocumentAsync(AdapterContext context, string externalId, CancellationToken ct);
}
