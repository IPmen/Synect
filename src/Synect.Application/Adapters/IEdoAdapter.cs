using Synect.Domain.Edo;

namespace Synect.Application.Adapters;

/// <param name="SupportsUpdatedSince">The provider can filter the list by last update time.</param>
/// <param name="PageSize">Number of documents per page.</param>
public sealed record EdoAdapterCapabilities(bool SupportsUpdatedSince, int PageSize);

/// <summary>An EDO document in the common model. Amounts are in minor units.</summary>
/// <param name="Currency">ISO 4217 alphabetic code; null means the provider default (UZS for Didox).</param>
/// <param name="SellerAccount">Seller's bank account written in the document, when the provider gives it.</param>
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
    string? StatusComment,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    RawRecord Raw,
    string? Currency = null,
    string? SellerAccount = null);

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
