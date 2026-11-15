using System.Reflection;
using System.Text.Json;
using Synect.Application.Ingest;

namespace Synect.Tests.Ingest;

/// <summary>Parsing of contract values and items without a database.</summary>
public class IngestParserTests
{
    private static readonly IngestOptions Options = new();
    private static readonly DateTimeOffset Exported = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static ParsedItem Parse(string entity, string json) =>
        new IngestItemParser(IngestSchema.Find(entity)!, Options).Parse(0, J(json), Exported);

    [Theory]
    [InlineData("\"1234.5\"", 123450L)]
    [InlineData("\"1 234,50\"", 123450L)]
    [InlineData("1234.50", 123450L)]
    [InlineData("\"-0.01\"", -1L)]
    [InlineData("0", 0L)]
    [InlineData("\"1 000\"", 100000L)]
    public void Money_is_converted_to_minor_units(string json, long minor) => Assert.Equal(minor, IngestValues.Money(J(json), "amount"));

    [Theory]
    [InlineData("\"1.001\"", IngestCodes.FractionalMinorUnits)]
    [InlineData("0.005", IngestCodes.FractionalMinorUnits)]
    [InlineData("\"abc\"", IngestCodes.InvalidAmount)]
    [InlineData("\"1,234.5\"", IngestCodes.InvalidAmount)]
    [InlineData("true", IngestCodes.InvalidAmount)]
    [InlineData("\"999999999999999999999\"", IngestCodes.AmountOutOfRange)]
    public void Invalid_money_is_refused(string json, string code) =>
        Assert.Equal(code, Assert.Throws<IngestValueException>(() => IngestValues.Money(J(json), "amount")).Code);

    [Theory]
    [InlineData("\"2026-11-01\"", "2026-11-01")]
    [InlineData("\"2026-11-01T00:00:00\"", "2026-11-01")]
    [InlineData("\"2026-11-01T23:59:59+05:00\"", "2026-11-01")]
    [InlineData("\"0001-01-01T00:00:00\"", null)]
    public void Dates_take_the_date_part_and_the_empty_1c_date_is_missing(string json, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), IngestValues.Date(J(json), "date"));

    [Theory]
    [InlineData("\"01.11.2026\"")]
    [InlineData("\"2026-02-30\"")]
    [InlineData("20261101")]
    [InlineData("\"1800-01-01\"")]
    public void Invalid_dates_are_refused(string json) =>
        Assert.Equal(IngestCodes.InvalidDate, Assert.Throws<IngestValueException>(() => IngestValues.Date(J(json), "date")).Code);

    [Fact]
    public void Timestamps_without_offset_are_tashkent_time()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), IngestValues.Timestamp(J("\"2026-11-01T10:00:00\""), "t", TimeSpan.FromHours(5)));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), IngestValues.Timestamp(J("\"2026-11-01T10:00:00Z\""), "t", TimeSpan.FromHours(5)));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), IngestValues.Timestamp(J("\"2026-11-01T10:00:00.123+03:00\""), "t", TimeSpan.FromHours(5))!.Value.AddTicks(-1_230_000));
        Assert.Null(IngestValues.Timestamp(J("\"0001-01-01T00:00:00\""), "t", TimeSpan.FromHours(5)));
    }

    [Theory]
    [InlineData("\"860\"", "UZS")]
    [InlineData("860", "UZS")]
    [InlineData("\"usd\"", "USD")]
    [InlineData("\"сум\"", "UZS")]
    [InlineData("\"руб.\"", "RUB")]
    [InlineData("\"978\"", "EUR")]
    public void Currencies_become_letter_codes(string json, string code) => Assert.Equal(code, IngestValues.Currency(J(json), "currency"));

    [Theory]
    [InlineData("\"999\"")]
    [InlineData("\"US\"")]
    [InlineData("\"доллары США\"")]
    public void Unknown_currencies_are_refused(string json) =>
        Assert.Equal(IngestCodes.InvalidCurrency, Assert.Throws<IngestValueException>(() => IngestValues.Currency(J(json), "currency")).Code);

    [Theory]
    [InlineData("\"301000001\"", true)]
    [InlineData("\"12345678901234\"", true)]
    [InlineData("301000001", true)]
    [InlineData("\"30100000\"", false)]
    [InlineData("\"3010000011\"", false)]
    [InlineData("\"30100000A\"", false)]
    public void Tins_have_9_or_14_digits(string json, bool valid)
    {
        if (valid) Assert.Equal(J(json).ValueKind == JsonValueKind.String ? J(json).GetString() : J(json).GetRawText(), IngestValues.Tin(J(json), "tin"));
        else Assert.Equal(IngestCodes.InvalidTin, Assert.Throws<IngestValueException>(() => IngestValues.Tin(J(json), "tin")).Code);
    }

    [Fact]
    public void Field_names_ignore_case_and_separators_and_unknown_fields_go_to_extra()
    {
        var item = Parse("counterparties", """{"Ref":"r1","NAME":"N","full-name":"F","Inn":"301000001","CustomField":{"a":1},"ModifiedAt":"2026-11-01T10:00:00"}""");
        Assert.True(item.IsValid, item.ErrorMessage);
        Assert.Equal("r1", item.Key);
        Assert.Equal("N", item.Values["name"]);
        Assert.Equal("F", item.Values["full_name"]);
        Assert.Equal("301000001", item.Values["tin"]);
        Assert.Equal("""{"CustomField":{"a":1}}""", item.ExtraJson);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), item.ModifiedAt);
    }

    [Fact]
    public void Missing_modified_at_falls_back_to_exported_at_and_missing_currency_to_the_default()
    {
        var item = Parse("payment_orders", """{"ref":"p","number":"1","date":"2026-11-01","amount":"5"}""");
        Assert.True(item.IsValid, item.ErrorMessage);
        Assert.Equal(Exported, item.ModifiedAt);
        Assert.Equal("UZS", item.Values["currency"]);
        Assert.Equal(false, item.Values["posted"]);
        Assert.Equal(500L, item.Values["amount_minor"]);
    }

    [Fact]
    public void Hash_ignores_key_order_and_formatting()
    {
        var a = Parse("counterparties", """{"ref":"r","name":"N"}""");
        var b = Parse("counterparties", """{ "name" : "N",  "ref" : "r" }""");
        var c = Parse("counterparties", """{"ref":"r","name":"M"}""");
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void Deletion_notice_needs_only_the_ref()
    {
        var notice = Parse("documents", """{"ref":"d","is_deleted":true}""");
        Assert.True(notice.IsValid);
        Assert.True(notice.IsDeletionNotice);
        var notDeleted = Parse("documents", """{"ref":"d","is_deleted":false}""");
        Assert.Equal(IngestCodes.MissingField, notDeleted.ErrorCode);
        // Balances have no ref: a deletion notice is not possible, the key fields are required.
        Assert.Equal(IngestCodes.MissingField, Parse("bank_account_balances", """{"bank_account_ref":"b","is_deleted":true}""").ErrorCode);
    }

    [Fact]
    public void Balance_keys_include_nulls_and_the_default_currency()
    {
        var item = Parse("settlement_balances", """{"as_of_date":"2026-10-31","counterparty_ref":"c","gl_account_code":"4010","balance_debit":1}""");
        Assert.True(item.IsValid, item.ErrorMessage);
        Assert.Equal("2026-10-31\u001fc\u001f\u001f4010\u001fUZS", item.Key);
    }

    [Fact]
    public void Lines_are_numbered_and_repeated_numbers_refused()
    {
        var item = Parse("documents", """{"ref":"d","doc_kind":"sales","number":"1","date":"2026-11-01","posted":"да","amount_total":2,"lines":[{"amount":1},{"amount":1,"extra_field":"x"}]}""");
        Assert.True(item.IsValid, item.ErrorMessage);
        Assert.Equal("Sales", item.Values["doc_kind"]);
        Assert.Equal(true, item.Values["posted"]);
        Assert.Equal([1, 2], item.Children!.Select(c => (int)c["line_no"]!).ToList());
        Assert.Equal("""{"extra_field":"x"}""", item.Children![1]["extra"]);

        var repeated = Parse("documents", """{"ref":"d","doc_kind":"Sales","number":"1","date":"2026-11-01","posted":true,"amount_total":2,"lines":[{"line_no":1,"amount":1},{"line_no":1,"amount":1}]}""");
        Assert.Equal(IngestCodes.DuplicateLineNo, repeated.ErrorCode);
    }

    [Fact]
    public void Envelope_is_checked_schema_version_first()
    {
        var root = J("""{"schema_version":7,"entity":"nonsense"}""");
        var ex = Assert.Throws<IngestEnvelopeException>(() => IngestEnvelopeParser.Parse(root, Options, "301000001"));
        Assert.Equal(IngestEnvelopeCodes.UnsupportedSchemaVersion, ex.Code);
        Assert.Equal(400, ex.HttpStatus);
    }

    [Fact]
    public void Every_required_field_is_stored_in_a_not_null_column_and_every_entity_has_a_key()
    {
        // The schema and the entities must agree: a required field maps to a non-nullable property.
        var types = typeof(Synect.Domain.Accounting.AccountingCounterparty).Assembly.GetTypes();
        foreach (var spec in IngestSchema.Entities)
        {
            Assert.NotEmpty(spec.Keys);
            var entity = spec.Group == Synect.Domain.Accounting.IngestDataGroup.Catalog
                ? types.Single(t => t.Name == spec.Table switch
                {
                    "catalog_items" => "CatalogItem", "catalog_categories" => "CatalogCategory", "catalog_price_types" => "CatalogPriceType",
                    "catalog_prices" => "CatalogPrice", "catalog_warehouses" => "CatalogWarehouse", "catalog_stock" => "CatalogStock",
                    _ => throw new InvalidOperationException(spec.Table),
                })
                : types.Single(t => t.Name == "Accounting" + string.Concat(spec.Name.TrimEnd('s').Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]))
                    || (spec.Name == "counterparties" && t.Name == "AccountingCounterparty"));
            foreach (var field in spec.DataFields)
            {
                var property = entity.GetProperties().SingleOrDefault(p => Snake(p.Name) == field.Column);
                Assert.True(property is not null, $"{spec.Name}.{field.Column} has no property");
                var nullable = Nullable.GetUnderlyingType(property!.PropertyType) is not null
                               || new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;
                if (field.Required || field.Default != IngestFieldDefault.None)
                    Assert.False(nullable, $"{spec.Name}.{field.Column} is required (or always filled) but nullable");
            }
        }
    }

    private static string Snake(string name) => string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
