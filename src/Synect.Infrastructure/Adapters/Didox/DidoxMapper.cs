using System.Text.Json;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Common;
using Synect.Domain.Edo;

namespace Synect.Infrastructure.Adapters.Didox;

public sealed class DidoxMapperOptions
{
    /// <summary>Didox totals are expected in sum with a fractional part; converted to tiyin.</summary>
    public AmountUnit SumUnit { get; set; } = AmountUnit.Major;

    /// <summary>Offset for timestamps without an explicit zone (Tashkent, UTC+5).</summary>
    public TimeSpan SourceUtcOffset { get; set; } = TimeSpan.FromHours(5);
}

public sealed record DidoxDocumentList(IReadOnlyList<EdoDocumentInfo> Items, int? Total);

/// <summary>Converts Didox document JSON into the common EDO model.</summary>
public sealed class DidoxMapper(IOptions<DidoxMapperOptions> options)
{
    public const string DocumentObjectType = "didox.document";

    private readonly DidoxMapperOptions _options = options.Value;

    /// <summary>Maps a document list response: an array, or { "data": [...], "total": n }.</summary>
    public DidoxDocumentList MapDocumentList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = SourceJson.Unwrap(root);
        var items = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(MapDocument).ToList()
            : [];
        var total = root.ValueKind == JsonValueKind.Object ? SourceJson.Int(root, "total") : null;
        return new DidoxDocumentList(items, total);
    }

    /// <summary>Maps a single document: the object itself or { "data": {...} }.</summary>
    public EdoDocumentInfo MapSingleDocument(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MapDocument(SourceJson.Unwrap(doc.RootElement));
    }

    private EdoDocumentInfo MapDocument(JsonElement item)
    {
        var id = SourceJson.Str(item, "doc_id") ?? throw new FormatException("Didox document without doc_id.");
        var owner = SourceJson.Int(item, "owner") switch
        {
            0 => EdoDocumentOwner.Incoming,
            1 => EdoDocumentOwner.Outgoing,
            var other => throw new FormatException($"Unknown Didox owner '{other}' for document {id}."),
        };
        // Raw code is kept as is; DidoxStatuses gives its meaning when known.
        var status = SourceJson.Str(item, "doc_status") ?? throw new FormatException($"Didox document {id} without doc_status.");

        return new EdoDocumentInfo(
            ExternalId: id,
            DocType: NormalizeDocType(SourceJson.Str(item, "doctype")),
            Status: status,
            Owner: owner,
            // "name" in the Didox list is the document number (confirmed by Didox documentation).
            Number: SourceJson.Str(item, "name"),
            DocumentDate: SourceJson.Date(item, "doc_date"),
            CounterpartyInn: SourceJson.Str(item, "partnerTin"),
            CounterpartyName: SourceJson.Str(item, "partnerCompany"),
            ContractNumber: SourceJson.Str(item, "contract_number"),
            ContractDate: SourceJson.Date(item, "contract_date"),
            TotalSum: Minor(item, "total_sum"),
            TotalVatSum: Minor(item, "total_vat_sum"),
            TotalDeliverySum: Minor(item, "total_delivery_sum"),
            TotalDeliverySumWithVat: Minor(item, "total_delivery_sum_with_vat"),
            StatusComment: SourceJson.Str(item, "status_comment"),
            CreatedAt: SourceJson.Timestamp(item, "created", _options.SourceUtcOffset),
            UpdatedAt: SourceJson.Timestamp(item, "updated", _options.SourceUtcOffset),
            Raw: new RawRecord(DocumentObjectType, id, item.GetRawText()),
            // Currency: not confirmed by documentation, verify on a real response. The Didox document list description
            // has no currency field, so "currency" and "currency_code" are read only if present (both optional, ISO
            // letter or numeric code); when neither is present the document is taken as UZS.
            Currency: IsoCurrency.Normalize(SourceJson.Str(item, "currency") ?? SourceJson.Str(item, "currency_code")) is { Length: > 0 } c ? c : "UZS",
            // sellerAccount is confirmed by the Didox documentation of the document list (no other spelling is guessed).
            SellerAccount: SourceJson.Str(item, "sellerAccount"));
    }

    private long Minor(JsonElement item, string field) =>
        SourceJson.Dec(item, field) is { } value ? SourceJson.ToMinorUnits(value, _options.SumUnit, field) : 0;

    /// <summary>Document type codes are three digits ("002"); numeric JSON values lose the leading zeros.</summary>
    private static string NormalizeDocType(string? value) =>
        value is null ? string.Empty
        : value.All(char.IsDigit) && value.Length < 3 ? value.PadLeft(3, '0')
        : value;
}
