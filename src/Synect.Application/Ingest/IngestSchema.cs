using Synect.Domain.Accounting;

namespace Synect.Application.Ingest;

/// <summary>Value kinds of the export contract fields and how they are checked (<see cref="IngestValues"/>).</summary>
public enum IngestFieldType
{
    /// <summary>A 1C reference: non-empty string of at most 64 characters; the empty 1C reference (zero GUID) counts as missing.</summary>
    Ref,

    /// <summary>Free third-party text (names, purpose, comment): stored as received, at most Ingest:MaxTextLength characters.</summary>
    Text,

    /// <summary>Short source text (kinds, statuses, types): at most 256 characters.</summary>
    ShortText,

    /// <summary>A number or code as text (document number, account number, GL account): at most 64 characters, trimmed.</summary>
    Code,

    /// <summary>INN / PINFL: 9 or 14 digits.</summary>
    Tin,

    /// <summary>ISO 8601 date (a date-time is accepted; its date part is taken); the empty 1C date 0001-01-01 counts as missing.</summary>
    Date,

    /// <summary>ISO 8601 date-time; without an offset Ingest:SourceUtcOffset is assumed.</summary>
    Timestamp,

    /// <summary>Decimal amount in currency units (sum), as a number or a string; stored in minor units (x100), fractional minor units refused.</summary>
    Money,

    /// <summary>Decimal (quantity).</summary>
    Decimal,

    /// <summary>VAT rate: a number ("12", "12%"); any other text (e.g. "Без НДС") is stored as NULL and kept in extra, never refused.</summary>
    Rate,

    /// <summary>Whole number 0..36500 (days).</summary>
    Int,

    /// <summary>true/false (also 1/0, "true"/"false", "да"/"нет").</summary>
    Bool,

    /// <summary>Currency: letter code, ISO numeric code or a known name; normalized to the letter code.</summary>
    Currency,

    /// <summary>Sales / Purchase.</summary>
    DocKind,

    /// <summary>Receipt / Payment.</summary>
    Direction,

    /// <summary>Barcodes: an array of strings of digits (a single string is one barcode); stored as text[], duplicates dropped.</summary>
    Barcodes,

    /// <summary>Goods / Service; any other text is stored as NULL and kept in extra, never refused.</summary>
    ItemType,
}

/// <summary>What an optional field gets when the item does not carry it.</summary>
public enum IngestFieldDefault
{
    /// <summary>NULL (or, for currencies, Ingest:DefaultCurrency).</summary>
    None,

    /// <summary>false (flags such as is_archived, is_default).</summary>
    False,

    /// <summary>exported_at of the package (the stock time of catalog_stock).</summary>
    ExportedAt,
}

/// <summary>
/// One field of the export contract: its logical name (as in the contract document), the column it is stored in, the
/// contract level (M = required, R = optional) and alternative spellings. Names are matched ignoring case, '_' and '-'
/// (counterparty_ref = CounterpartyRef = counterparty-ref). Anything not described here is stored in extra and in the raw
/// layer, never refused. Correcting the contract therefore means editing this table, not the receiver.
/// </summary>
public sealed record IngestField(string Name, string Column, IngestFieldType Type, bool Required, params string[] Aliases)
{
    /// <summary>Value of the column when the item does not carry the field.</summary>
    public IngestFieldDefault Default { get; init; }

    /// <summary>PostgreSQL type of the column, used in jsonb_to_recordset.</summary>
    public string PgType => Type switch
    {
        IngestFieldType.Date => "date",
        IngestFieldType.Timestamp => "timestamptz",
        IngestFieldType.Money => "bigint",
        IngestFieldType.Decimal or IngestFieldType.Rate => "numeric",
        IngestFieldType.Int => "integer",
        IngestFieldType.Bool => "boolean",
        IngestFieldType.Barcodes => "text[]",
        _ => "text",
    };
}

/// <summary>Nested rows of an item (document lines, payment settlements), replaced as a whole with their document.</summary>
public sealed record IngestChildSpec(string Name, string Table, string[] Aliases, IReadOnlyList<IngestField> Fields);

/// <summary>One entity of the envelope (counterparties, documents, ...).</summary>
public sealed class IngestEntitySpec
{
    public required string Name { get; init; }
    public required string Table { get; init; }

    /// <summary>Fields that identify a record within the organization: ref, or the key of a balance row.</summary>
    public required string[] KeyFields { get; init; }

    public required IReadOnlyList<IngestField> Fields { get; init; }
    public IngestChildSpec? Children { get; init; }

    /// <summary>Accounting or catalog: which freshness the entity's packages refresh (ingest_freshness).</summary>
    public IngestDataGroup Group { get; init; } = IngestDataGroup.Accounting;

    /// <summary>
    /// A current-state table keyed by two refs (catalog_prices, catalog_stock): a completed full export REMOVES the rows it
    /// did not contain (logged in ingest_removed_rows) instead of marking them deleted, and a deletion may carry only the key.
    /// </summary>
    public bool CurrentState { get; init; }

    /// <summary>Column filled by Synect with the search key of the "name" field (catalog_items.search_name); null for none.</summary>
    public string? SearchColumn { get; init; }

    /// <summary>The key includes the balance date: a full export covers only the dates it contained.</summary>
    public bool KeyedByDate => KeyFields.Contains("as_of_date");

    /// <summary>Object type in raw_objects: "1c.{entity}" (rejected items: "1c.{entity}.rejected").</summary>
    public string RawObjectType => "1c." + Name;

    public bool HasRef => KeyFields is ["ref"];

    public IReadOnlyList<IngestField> Keys => KeyFields.Select(k => Fields.Single(f => f.Name == k)).ToList();

    /// <summary>Columns the writer fills from an item: the data fields plus the search column computed by Synect.</summary>
    public IReadOnlyList<(string Column, string PgType)> WriteColumns =>
        [.. DataFields.Select(f => (f.Column, f.PgType)), .. SearchColumn is null ? [] : new[] { (SearchColumn, "text") }];

    /// <summary>Fields stored in their own columns (everything except the common columns handled by the writer).</summary>
    public IReadOnlyList<IngestField> DataFields =>
        Fields.Where(f => f.Name is not (IngestSchema.ModifiedAt or IngestSchema.IsDeleted)).ToList();
}

/// <summary>
/// The logical fields of "Контракт выгрузки данных 1С в Synect", schema_version 1, per entity. Required (M) fields have
/// NOT NULL columns; optional (R) fields nullable ones (currency is filled with Ingest:DefaultCurrency when absent).
/// </summary>
public static class IngestSchema
{
    public const string ModifiedAt = "modified_at";
    public const string IsDeleted = "is_deleted";

    private static IngestField F(string name, IngestFieldType type, bool required, params string[] aliases) =>
        new(name, name, type, required, aliases);

    private static IngestField Money(string name, bool required, params string[] aliases) =>
        new(name, name + "_minor", IngestFieldType.Money, required, aliases);

    private static IngestField[] Common(params IngestField[] fields) =>
    [
        F("ref", IngestFieldType.Ref, true, "id", "guid", "uuid", "link"),
        .. fields,
        F(ModifiedAt, IngestFieldType.Timestamp, false, "modified", "updated_at", "changed_at", "data_version_at"),
        F(IsDeleted, IngestFieldType.Bool, false, "deletion_mark", "deleted", "marked_for_deletion"),
    ];

    private static IngestField[] Balance(params IngestField[] fields) =>
    [
        .. fields,
        F(ModifiedAt, IngestFieldType.Timestamp, false, "modified", "updated_at", "changed_at"),
        F(IsDeleted, IngestFieldType.Bool, false, "deleted"),
    ];

    public static readonly IReadOnlyList<IngestEntitySpec> Entities =
    [
        new()
        {
            Name = "counterparties", Table = "accounting_counterparties", KeyFields = ["ref"],
            Fields = Common(
                F("tin", IngestFieldType.Tin, false, "inn", "stir", "pinfl"),
                F("name", IngestFieldType.Text, true, "description", "title"),
                F("full_name", IngestFieldType.Text, false, "name_full"),
                F("kind", IngestFieldType.ShortText, false, "legal_kind", "entity_kind")),
        },
        new()
        {
            Name = "contracts", Table = "accounting_contracts", KeyFields = ["ref"],
            Fields = Common(
                F("counterparty_ref", IngestFieldType.Ref, true, "owner_ref", "counterparty"),
                F("number", IngestFieldType.Code, false, "contract_number"),
                F("date", IngestFieldType.Date, false, "contract_date"),
                F("kind", IngestFieldType.ShortText, false, "contract_kind"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("valid_until", IngestFieldType.Date, false, "expiry_date", "end_date"),
                F("payment_term_days", IngestFieldType.Int, false, "payment_days")),
        },
        new()
        {
            Name = "bank_accounts", Table = "accounting_bank_accounts", KeyFields = ["ref"],
            Fields = Common(
                F("number", IngestFieldType.Code, true, "account_number"),
                F("bank_code", IngestFieldType.Code, false, "mfo", "bik", "bank_mfo"),
                F("bank_name", IngestFieldType.Text, false, "bank"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("gl_account_code", IngestFieldType.Code, false, "gl_account", "account_code"),
                F("is_active", IngestFieldType.Bool, false, "active")),
        },
        new()
        {
            Name = "documents", Table = "accounting_documents", KeyFields = ["ref"],
            Fields = Common(
                F("doc_kind", IngestFieldType.DocKind, true, "kind", "document_kind"),
                F("doc_type", IngestFieldType.ShortText, false, "type", "document_type"),
                F("number", IngestFieldType.Code, true, "document_number"),
                F("date", IngestFieldType.Date, true, "document_date"),
                F("posted", IngestFieldType.Bool, true, "is_posted"),
                F("counterparty_ref", IngestFieldType.Ref, false),
                F("counterparty_tin", IngestFieldType.Tin, false, "counterparty_inn"),
                F("contract_ref", IngestFieldType.Ref, false),
                F("contract_number", IngestFieldType.Code, false),
                F("contract_date", IngestFieldType.Date, false),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                Money("amount_total", true, "total", "amount", "sum_total"),
                Money("vat_amount", false, "vat", "vat_sum"),
                F("esf_number", IngestFieldType.Code, false, "invoice_number"),
                F("esf_date", IngestFieldType.Date, false, "invoice_date"),
                F("esf_id", IngestFieldType.Code, false, "invoice_id"),
                F("esf_status", IngestFieldType.Code, false, "invoice_status"),
                F("due_date", IngestFieldType.Date, false, "payment_due_date"),
                F("comment", IngestFieldType.Text, false, "note")),
            Children = new IngestChildSpec("lines", "accounting_document_lines", ["positions", "goods", "rows"],
            [
                F("line_no", IngestFieldType.Int, false, "line_number", "row_no"),
                F("name", IngestFieldType.Text, false, "description", "product"),
                F("product_code", IngestFieldType.ShortText, false, "code", "catalog_code", "ikpu"),
                F("quantity", IngestFieldType.Decimal, false, "qty"),
                Money("price", false),
                F("vat_rate", IngestFieldType.Rate, false, "vat_percent"),
                Money("amount", true, "sum", "total"),
            ]),
        },
        new()
        {
            Name = "bank_documents", Table = "accounting_bank_documents", KeyFields = ["ref"],
            Fields = Common(
                F("direction", IngestFieldType.Direction, true, "kind"),
                F("number", IngestFieldType.Code, true, "document_number"),
                F("date", IngestFieldType.Date, true, "document_date"),
                F("posted", IngestFieldType.Bool, true, "is_posted"),
                F("bank_account_ref", IngestFieldType.Ref, false),
                F("bank_account_number", IngestFieldType.Code, false, "account_number"),
                F("counterparty_ref", IngestFieldType.Ref, false),
                F("counterparty_tin", IngestFieldType.Tin, false, "counterparty_inn"),
                Money("amount", true, "sum"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("purpose", IngestFieldType.Text, false, "payment_purpose", "details"),
                F("contract_ref", IngestFieldType.Ref, false),
                F("bank_document_number", IngestFieldType.Code, false, "incoming_number"),
                F("bank_document_date", IngestFieldType.Date, false, "incoming_date"),
                F("cash_flow_item", IngestFieldType.ShortText, false, "cash_flow")),
            Children = new IngestChildSpec("settlements", "accounting_bank_document_settlements", ["settlement", "breakdown", "allocations"],
            [
                F("line_no", IngestFieldType.Int, false, "line_number", "row_no"),
                F("settled_document_ref", IngestFieldType.Ref, false, "document_ref", "settlement_document_ref"),
                Money("amount", true, "sum"),
            ]),
        },
        new()
        {
            Name = "payment_orders", Table = "accounting_payment_orders", KeyFields = ["ref"],
            Fields = Common(
                F("number", IngestFieldType.Code, true, "document_number"),
                F("date", IngestFieldType.Date, true, "document_date"),
                F("posted", IngestFieldType.Bool, false, "is_posted"),
                F("bank_account_ref", IngestFieldType.Ref, false),
                F("counterparty_tin", IngestFieldType.Tin, false, "counterparty_inn"),
                Money("amount", true, "sum"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("purpose", IngestFieldType.Text, false, "payment_purpose", "details"),
                F("bank_status", IngestFieldType.ShortText, false, "status")),
        },
        new()
        {
            Name = "settlement_balances", Table = "accounting_settlement_balances",
            KeyFields = ["as_of_date", "counterparty_ref", "contract_ref", "gl_account_code", "currency"],
            Fields = Balance(
                F("as_of_date", IngestFieldType.Date, true, "date", "balance_date"),
                F("counterparty_ref", IngestFieldType.Ref, false),
                F("counterparty_tin", IngestFieldType.Tin, false, "counterparty_inn"),
                F("contract_ref", IngestFieldType.Ref, false),
                F("gl_account_code", IngestFieldType.Code, true, "gl_account", "account_code"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                Money("balance_debit", false),
                Money("balance_credit", false),
                Money("turnover_debit", false),
                Money("turnover_credit", false)),
        },
        new()
        {
            Name = "bank_account_balances", Table = "accounting_bank_account_balances",
            KeyFields = ["as_of_date", "bank_account_ref"],
            Fields = Balance(
                F("as_of_date", IngestFieldType.Date, true, "date", "balance_date"),
                F("bank_account_ref", IngestFieldType.Ref, true, "account_ref"),
                Money("balance", true, "closing_balance"),
                Money("turnover_debit", false),
                Money("turnover_credit", false)),
        },

        // ---- Catalog for the Mailo bot: changes every minute, a full export once a day. Field names are not confirmed on
        // a real configuration (Розница, Управление торговлей, Бухгалтерия): aliases cover the usual 1C spellings.
        new()
        {
            Name = "items", Table = "catalog_items", KeyFields = ["ref"], Group = IngestDataGroup.Catalog, SearchColumn = "search_name",
            Fields = Common(
                F("item_type", IngestFieldType.ItemType, false, "type", "kind", "nomenclature_type"),
                F("code", IngestFieldType.Code, false, "item_code"),
                F("article", IngestFieldType.Code, false, "sku", "vendor_code", "artikul"),
                F("name", IngestFieldType.Text, true, "description", "title"),
                F("full_name", IngestFieldType.Text, false, "name_full", "print_name"),
                F("category_ref", IngestFieldType.Ref, false, "parent_ref", "group_ref", "category"),
                F("unit", IngestFieldType.ShortText, false, "unit_name", "measure", "base_unit"),
                F("barcodes", IngestFieldType.Barcodes, false, "barcode", "ean", "shtrihkod", "shtrikhkody"),
                F("vat_rate", IngestFieldType.Rate, false, "vat_percent", "vat"),
                F("is_archived", IngestFieldType.Bool, false, "archived", "not_used") with { Default = IngestFieldDefault.False }),
        },
        new()
        {
            Name = "categories", Table = "catalog_categories", KeyFields = ["ref"], Group = IngestDataGroup.Catalog,
            Fields = Common(
                F("parent_ref", IngestFieldType.Ref, false, "parent", "group_ref"),
                F("name", IngestFieldType.Text, true, "description", "title")),
        },
        new()
        {
            Name = "price_types", Table = "catalog_price_types", KeyFields = ["ref"], Group = IngestDataGroup.Catalog,
            Fields = Common(
                F("name", IngestFieldType.Text, true, "description", "title"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("is_default", IngestFieldType.Bool, false, "default", "is_retail") with { Default = IngestFieldDefault.False }),
        },
        new()
        {
            Name = "prices", Table = "catalog_prices", KeyFields = ["item_ref", "price_type_ref"], Group = IngestDataGroup.Catalog,
            CurrentState = true,
            Fields = Balance(
                F("item_ref", IngestFieldType.Ref, true, "nomenclature_ref", "product_ref"),
                F("price_type_ref", IngestFieldType.Ref, true, "price_kind_ref"),
                Money("price", true, "amount", "value"),
                F("currency", IngestFieldType.Currency, false, "currency_code"),
                F("effective_from", IngestFieldType.Date, false, "period", "date", "valid_from")),
        },
        new()
        {
            Name = "warehouses", Table = "catalog_warehouses", KeyFields = ["ref"], Group = IngestDataGroup.Catalog,
            Fields = Common(F("name", IngestFieldType.Text, true, "description", "title")),
        },
        new()
        {
            Name = "stock", Table = "catalog_stock", KeyFields = ["item_ref", "warehouse_ref"], Group = IngestDataGroup.Catalog,
            CurrentState = true,
            Fields = Balance(
                F("item_ref", IngestFieldType.Ref, true, "nomenclature_ref", "product_ref"),
                F("warehouse_ref", IngestFieldType.Ref, true, "store_ref", "warehouse"),
                F("quantity", IngestFieldType.Decimal, true, "qty", "balance", "on_hand"),
                F("reserved", IngestFieldType.Decimal, false, "reserve", "reserved_qty"),
                F("as_of", IngestFieldType.Timestamp, false, "as_of_time", "period", "stock_at") with { Default = IngestFieldDefault.ExportedAt }),
        },
    ];

    private static readonly Dictionary<string, IngestEntitySpec> ByName =
        Entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public static IngestEntitySpec? Find(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out var spec) ? spec : null;

    /// <summary>Name comparison form: lower case without '_', '-' and spaces.</summary>
    public static string NormalizeName(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 128)];
        var n = 0;
        foreach (var c in name)
        {
            if (c is '_' or '-' or ' ') continue;
            if (n == buffer.Length) break;
            buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..n]);
    }
}
