using Microsoft.Extensions.Options;
using Synect.Application.Common;
using Synect.Domain.Edo;
using Synect.Infrastructure.Adapters.Didox;

namespace Synect.Tests.Mapping;

public class DidoxMapperTests
{
    private static readonly DidoxMapper Mapper = new(Options.Create(new DidoxMapperOptions()));

    private const string ListJson = """
        {
          "data": [
            {
              "doc_id": "abc123", "name": "INV-15", "doc_date": "2026-10-20", "doc_status": 3, "doctype": 2,
              "owner": 1, "partnerTin": "305555555", "partnerCompany": "PARTNER LLC",
              "contract_number": "C-7", "contract_date": "2026-01-15",
              "total_sum": 1120000.50, "total_vat_sum": 120000.05, "total_delivery_sum": 1000000.45,
              "total_delivery_sum_with_vat": 1120000.50,
              "created": "2026-10-20 09:15:00", "updated": "2026-10-21 14:00:00"
            },
            {
              "doc_id": "def456", "name": "ACT-3", "doc_date": "20.10.2026", "doc_status": "40", "doctype": "052",
              "owner": 0, "partnerTin": "306666666", "partnerCompany": "OTHER LLC",
              "contract_number": null, "contract_date": null,
              "total_sum": "500", "total_vat_sum": 0, "total_delivery_sum": 500, "total_delivery_sum_with_vat": 500,
              "created": 1792990800, "updated": 1792990800000
            }
          ],
          "total": 2
        }
        """;

    [Fact]
    public void Maps_document_list_fields()
    {
        var list = Mapper.MapDocumentList(ListJson);
        Assert.Equal(2, list.Total);
        var doc = list.Items[0];

        Assert.Equal("abc123", doc.ExternalId);
        Assert.Equal("002", doc.DocType);
        Assert.Equal("3", doc.Status);
        Assert.Equal(EdoDocumentOwner.Outgoing, doc.Owner);
        Assert.Equal("INV-15", doc.Number);
        Assert.Equal(new DateOnly(2026, 10, 20), doc.DocumentDate);
        Assert.Equal("305555555", doc.CounterpartyInn);
        Assert.Equal("PARTNER LLC", doc.CounterpartyName);
        Assert.Equal("C-7", doc.ContractNumber);
        Assert.Equal(new DateOnly(2026, 1, 15), doc.ContractDate);
        Assert.Equal(DidoxMapper.DocumentObjectType, doc.Raw.ObjectType);
    }

    [Fact]
    public void Sums_are_converted_to_tiyin()
    {
        var doc = Mapper.MapDocumentList(ListJson).Items[0];
        Assert.Equal(112_000_050L, doc.TotalSum);
        Assert.Equal(12_000_005L, doc.TotalVatSum);
        Assert.Equal(100_000_045L, doc.TotalDeliverySum);
        Assert.Equal(112_000_050L, doc.TotalDeliverySumWithVat);
    }

    [Fact]
    public void Local_timestamps_are_read_as_Tashkent_time()
    {
        var doc = Mapper.MapDocumentList(ListJson).Items[0];
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 4, 15, 0, TimeSpan.Zero), doc.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero), doc.UpdatedAt);
    }

    [Fact]
    public void Tolerates_string_numbers_unix_times_and_nulls()
    {
        var doc = Mapper.MapDocumentList(ListJson).Items[1];
        Assert.Equal(EdoDocumentOwner.Incoming, doc.Owner);
        Assert.Equal("40", doc.Status);
        Assert.Equal("052", doc.DocType);
        Assert.Equal(new DateOnly(2026, 10, 20), doc.DocumentDate);
        Assert.Equal(50_000L, doc.TotalSum);
        Assert.Null(doc.ContractNumber);
        Assert.Null(doc.ContractDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792990800), doc.CreatedAt);
        Assert.Equal(doc.CreatedAt, doc.UpdatedAt);
    }

    [Fact]
    public void Maps_single_document_envelope()
    {
        var doc = Mapper.MapSingleDocument("""{"data":{"doc_id":"x1","doc_status":0,"doctype":"005","owner":0}}""");
        Assert.Equal("x1", doc.ExternalId);
        Assert.Equal("0", doc.Status);
        Assert.Equal(0, doc.TotalSum);
    }
}

public class JsonHasherTests
{
    [Fact]
    public void Hash_ignores_key_order_and_whitespace()
    {
        Assert.Equal(JsonHasher.Hash("""{"a":1,"b":{"c":2,"d":[1,2]}}"""),
                     JsonHasher.Hash("""{ "b": { "d": [1, 2], "c": 2 }, "a": 1 }"""));
    }

    [Fact]
    public void Hash_detects_value_changes()
    {
        Assert.NotEqual(JsonHasher.Hash("""{"status":1}"""), JsonHasher.Hash("""{"status":3}"""));
        Assert.NotEqual(JsonHasher.Hash("""{"l":[1,2]}"""), JsonHasher.Hash("""{"l":[2,1]}"""));
    }
}
