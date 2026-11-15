using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>Answer of the receiver to one package.</summary>
public sealed record SampleResponse(HttpStatusCode Status, JsonElement Body, TimeSpan Elapsed)
{
    public string? Get(string name) => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
        : null;

    public int Int(string name) => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : -1;

    /// <summary>Per-item status ("accepted", "unchanged", "rejected") and code, by index.</summary>
    public IReadOnlyList<(string Status, string? Code)> Items =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(i => (i.GetProperty("status").GetString()!, i.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)).ToList()
            : [];
}

/// <summary>One step of a sample scenario: what was sent, what was expected and what came back.</summary>
public sealed record SampleStep(string Name, string Expected, string Actual, bool Ok, TimeSpan Elapsed);

/// <summary>
/// A fake "1C extension" (development and tests only): sends packages to POST /ingest/1c/v1/batches over HTTP with an ingest
/// key, exactly like the real extension would (JSON, optionally gzip, Retry-After honoured on 429). The data is synthetic.
/// </summary>
public sealed class OneCSampleClient(HttpClient http, string key, string organizationTin)
{
    public const string Route = "/ingest/1c/v1/batches";

    public string OrganizationTin => organizationTin;

    /// <summary>Builds an envelope of schema_version 1.</summary>
    public Dictionary<string, object?> Envelope(string entity, string batchId, int sequence, IEnumerable<object> items, string mode = "delta",
        string? fullSyncId = null, bool? isLast = null, int schemaVersion = 1, DateTimeOffset? exportedAt = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["schema_version"] = schemaVersion,
            ["extension_version"] = "sample-1.0",
            ["organization_tin"] = organizationTin,
            ["batch_id"] = batchId,
            ["sequence"] = sequence,
            ["exported_at"] = (exportedAt ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            ["mode"] = mode,
            ["entity"] = entity,
            ["items"] = items.ToList(),
        };
        if (fullSyncId is not null) envelope["full_sync_id"] = fullSyncId;
        if (isLast is not null) envelope["is_last_batch"] = isLast;
        return envelope;
    }

    public Task<SampleResponse> SendAsync(object envelope, bool gzip = false, CancellationToken ct = default) =>
        SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(envelope), gzip, ct);

    /// <summary>Sends a body as is; on 429 waits for Retry-After (at most 3 times).</summary>
    public async Task<SampleResponse> SendRawAsync(byte[] body, bool gzip = false, CancellationToken ct = default, string contentType = "application/json")
    {
        for (var attempt = 0; ; attempt++)
        {
            var started = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, Route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            byte[] payload = body;
            if (gzip)
            {
                using var buffer = new MemoryStream();
                await using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) await zip.WriteAsync(body, ct);
                payload = buffer.ToArray();
            }
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (gzip) request.Content.Headers.ContentEncoding.Add("gzip");
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            started.Stop();
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3 && response.Headers.RetryAfter?.Delta is { } wait)
            {
                await Task.Delay(wait, ct);
                continue;
            }
            var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
            return new SampleResponse(response.StatusCode, json, started.Elapsed);
        }
    }

    // ---- Synthetic items ---------------------------------------------------------------------------------------------

    private static string At(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static Dictionary<string, object?> Counterparty(string reference, string name, string? tin, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["tin"] = tin, ["name"] = name, ["full_name"] = name + " MChJ", ["kind"] = "ЮрЛицо", ["is_deleted"] = false,
        ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Contract(string reference, string counterpartyRef, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["counterparty_ref"] = counterpartyRef, ["number"] = "Д-" + reference, ["date"] = "2026-01-10", ["kind"] = "СПокупателем",
        ["currency"] = "860", ["valid_until"] = "2026-12-31", ["payment_term_days"] = 30, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> BankAccount(string reference, string number, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["number"] = number, ["bank_code"] = "00974", ["bank_name"] = "Sample bank", ["currency"] = "UZS",
        ["gl_account_code"] = "5110", ["is_active"] = true, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Document(string reference, string kind, string counterpartyRef, string? tin, decimal amount,
        DateTimeOffset modified, bool posted = true, int lines = 2) => new()
    {
        ["ref"] = reference, ["doc_kind"] = kind, ["doc_type"] = kind == "Sales" ? "РеализацияТоваровУслуг" : "ПоступлениеТоваровУслуг",
        ["number"] = "N-" + reference, ["date"] = "2026-10-15", ["posted"] = posted, ["is_deleted"] = false,
        ["counterparty_ref"] = counterpartyRef, ["counterparty_tin"] = tin, ["contract_ref"] = null, ["currency"] = "UZS",
        ["amount_total"] = amount.ToString("0.00", CultureInfo.InvariantCulture), ["vat_amount"] = Math.Round(amount * 12m / 112m, 2),
        ["esf_number"] = "ESF-" + reference, ["esf_date"] = "2026-10-15", ["due_date"] = "2026-11-14", ["comment"] = "sample",
        ["modified_at"] = At(modified),
        ["lines"] = Enumerable.Range(1, lines).Select(i => new Dictionary<string, object?>
        {
            ["line_no"] = i, ["name"] = "Item " + i, ["product_code"] = "0610100100000000" + i, ["quantity"] = 1.5m * i,
            ["price"] = Math.Round(amount / lines / (1.5m * i), 2), ["vat_rate"] = i == 1 ? "12" : "Без НДС",
            ["amount"] = (amount / lines).ToString("0.00", CultureInfo.InvariantCulture),
        }).ToList(),
    };

    public static Dictionary<string, object?> BankDocument(string reference, string direction, string counterpartyRef, string? tin, decimal amount,
        DateTimeOffset modified, string? settledRef = null) => new()
    {
        ["ref"] = reference, ["direction"] = direction, ["number"] = "B-" + reference, ["date"] = "2026-10-20", ["posted"] = true,
        ["bank_account_ref"] = "ba-001", ["bank_account_number"] = "20208000900100001001", ["counterparty_ref"] = counterpartyRef,
        ["counterparty_tin"] = tin, ["amount"] = amount, ["currency"] = "UZS", ["purpose"] = "Оплата по счёт-фактуре № N-" + (settledRef ?? reference),
        ["cash_flow_item"] = "Оплата поставщикам", ["modified_at"] = At(modified),
        ["settlements"] = new[] { new Dictionary<string, object?> { ["settled_document_ref"] = settledRef, ["amount"] = amount } },
    };

    public static Dictionary<string, object?> PaymentOrder(string reference, string? tin, decimal amount, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["number"] = "P-" + reference, ["date"] = "2026-10-21", ["posted"] = true, ["bank_account_ref"] = "ba-001",
        ["counterparty_tin"] = tin, ["amount"] = amount, ["currency"] = "UZS", ["purpose"] = "Оплата", ["bank_status"] = "Отправлено",
        ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> SettlementBalance(string date, string counterpartyRef, string? contractRef, string gl, decimal debit) => new()
    {
        ["as_of_date"] = date, ["counterparty_ref"] = counterpartyRef, ["contract_ref"] = contractRef, ["gl_account_code"] = gl, ["currency"] = "UZS",
        ["balance_debit"] = debit, ["balance_credit"] = 0, ["turnover_debit"] = debit, ["turnover_credit"] = 0,
    };

    public static Dictionary<string, object?> BankAccountBalance(string date, string accountRef, decimal balance) => new()
    {
        ["as_of_date"] = date, ["bank_account_ref"] = accountRef, ["balance"] = balance, ["turnover_debit"] = 0, ["turnover_credit"] = balance,
    };

    // ---- Catalog for the Mailo bot -------------------------------------------------------------------------------------

    public static Dictionary<string, object?> CatalogItem(string reference, string name, string? categoryRef, DateTimeOffset modified,
        string type = "Goods", params string[] barcodes) => new()
    {
        ["ref"] = reference, ["item_type"] = type, ["code"] = "C-" + reference, ["article"] = "A-" + reference, ["name"] = name,
        ["full_name"] = name + " (полное)", ["category_ref"] = categoryRef, ["unit"] = type == "Service" ? "усл" : "шт",
        ["barcodes"] = barcodes, ["vat_rate"] = "12", ["is_archived"] = false, ["is_deleted"] = false, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Category(string reference, string name, string? parentRef, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["parent_ref"] = parentRef, ["name"] = name, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> PriceType(string reference, string name, bool isDefault, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["name"] = name, ["currency"] = "860", ["is_default"] = isDefault, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Warehouse(string reference, string name, DateTimeOffset modified) => new()
    {
        ["ref"] = reference, ["name"] = name, ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Price(string itemRef, string priceTypeRef, decimal price, DateTimeOffset modified) => new()
    {
        ["item_ref"] = itemRef, ["price_type_ref"] = priceTypeRef, ["price"] = price.ToString("0.00", CultureInfo.InvariantCulture),
        ["currency"] = "UZS", ["effective_from"] = "2026-11-01", ["modified_at"] = At(modified),
    };

    public static Dictionary<string, object?> Stock(string itemRef, string warehouseRef, decimal quantity, DateTimeOffset modified, decimal? reserved = null) => new()
    {
        ["item_ref"] = itemRef, ["warehouse_ref"] = warehouseRef, ["quantity"] = quantity, ["reserved"] = reserved,
        ["as_of"] = At(modified), ["modified_at"] = At(modified),
    };

    /// <summary>A valid synthetic EAN-13-like barcode (13 digits) for sample number <paramref name="n"/>.</summary>
    public static string Barcode(int n) => (4_780_000_000_000L + n).ToString(CultureInfo.InvariantCulture);

    /// <summary>A valid synthetic TIN (9 digits) for sample number <paramref name="n"/>.</summary>
    public static string Tin(int n) => (300_000_000 + n % 99_999_999).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Scenarios of send-1c-sample: basic, messy, catalog-minute and large (see the DevTools command).</summary>
public sealed class OneCSampleScenarios(OneCSampleClient client)
{
    private readonly List<SampleStep> _steps = [];
    private readonly string _run = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];

    public IReadOnlyList<SampleStep> Steps => _steps;

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(5)).AddMinutes(minutes);

    private async Task<SampleResponse> StepAsync(string name, string expected, object envelope, Func<SampleResponse, bool> check,
        bool gzip = false, CancellationToken ct = default)
    {
        var r = await client.SendAsync(envelope, gzip, ct);
        var actual = $"HTTP {(int)r.Status} {r.Get("status")} accepted={r.Int("accepted")} unchanged={r.Int("unchanged")} rejected={r.Int("rejected")}" +
                     (r.Get("error") is { } e ? $" error={e}" : "") + (r.Int("marked_deleted") > 0 ? $" marked_deleted={r.Int("marked_deleted")}" : "");
        _steps.Add(new SampleStep(name, expected, actual, check(r), r.Elapsed));
        return r;
    }

    /// <summary>Normal data of every entity. Fixed refs: a second run answers "unchanged".</summary>
    public async Task BasicAsync(CancellationToken ct = default)
    {
        var b = "basic-" + _run;
        var ok = (SampleResponse r) => r.Status == HttpStatusCode.OK && r.Int("rejected") == 0;
        await StepAsync("counterparties", "accepted (or unchanged on a repeated run)", client.Envelope("counterparties", b + "-cp", 1,
            Enumerable.Range(1, 5).Select(i => OneCSampleClient.Counterparty($"cp-{i:000}", $"Sample counterparty {i}", OneCSampleClient.Tin(i), T(0)))), ok, ct: ct);
        await StepAsync("contracts", "accepted", client.Envelope("contracts", b + "-ct", 1,
            Enumerable.Range(1, 3).Select(i => OneCSampleClient.Contract($"ct-{i:000}", $"cp-{i:000}", T(0)))), ok, ct: ct);
        await StepAsync("bank_accounts", "accepted", client.Envelope("bank_accounts", b + "-ba", 1,
            new[] { OneCSampleClient.BankAccount("ba-001", "20208000900100001001", T(0)), OneCSampleClient.BankAccount("ba-002", "20208840900100001002", T(0)) }), ok, ct: ct);
        await StepAsync("documents (with lines, gzip)", "accepted", client.Envelope("documents", b + "-doc", 1,
            Enumerable.Range(1, 4).Select(i => OneCSampleClient.Document($"doc-{i:000}", i <= 2 ? "Sales" : "Purchase", $"cp-{i:000}", OneCSampleClient.Tin(i), 1_000_000m * i, T(0)))),
            ok, gzip: true, ct: ct);
        await StepAsync("bank_documents (with settlements)", "accepted", client.Envelope("bank_documents", b + "-bd", 1,
            Enumerable.Range(1, 3).Select(i => OneCSampleClient.BankDocument($"bd-{i:000}", i == 3 ? "Payment" : "Receipt", $"cp-{i:000}", OneCSampleClient.Tin(i), 1_000_000m * i, T(0), $"doc-{i:000}"))), ok, ct: ct);
        await StepAsync("payment_orders", "accepted", client.Envelope("payment_orders", b + "-po", 1,
            Enumerable.Range(1, 2).Select(i => OneCSampleClient.PaymentOrder($"po-{i:000}", OneCSampleClient.Tin(i), 250_000.5m * i, T(0)))), ok, ct: ct);
        await StepAsync("settlement_balances", "accepted", client.Envelope("settlement_balances", b + "-sb", 1, new[]
        {
            OneCSampleClient.SettlementBalance("2026-10-31", "cp-001", "ct-001", "4010", 1_000_000m),
            OneCSampleClient.SettlementBalance("2026-10-31", "cp-002", null, "4010", 2_000_000m),
            OneCSampleClient.SettlementBalance("2026-10-31", "cp-003", "ct-003", "6010", 300_000m),
        }), ok, ct: ct);
        await StepAsync("bank_account_balances", "accepted", client.Envelope("bank_account_balances", b + "-bb", 1, new[]
        {
            OneCSampleClient.BankAccountBalance("2026-10-31", "ba-001", 12_345_678.9m), OneCSampleClient.BankAccountBalance("2026-10-31", "ba-002", 1000m),
        }), ok, ct: ct);

        // Catalog for the Mailo bot.
        await StepAsync("catalog categories", "accepted", client.Envelope("categories", b + "-cat", 1, new[]
        {
            OneCSampleClient.Category("cat-root", "Товары", null, T(0)), OneCSampleClient.Category("cat-drinks", "Напитки", "cat-root", T(0)),
            OneCSampleClient.Category("cat-services", "Услуги", null, T(0)),
        }), ok, ct: ct);
        await StepAsync("price types", "accepted", client.Envelope("price_types", b + "-pt", 1, new[]
        {
            OneCSampleClient.PriceType("pt-retail", "Розничная", true, T(0)), OneCSampleClient.PriceType("pt-wholesale", "Оптовая", false, T(0)),
        }), ok, ct: ct);
        await StepAsync("warehouses", "accepted", client.Envelope("warehouses", b + "-wh", 1, new[]
        {
            OneCSampleClient.Warehouse("wh-main", "Основной склад", T(0)), OneCSampleClient.Warehouse("wh-shop", "Магазин", T(0)),
        }), ok, ct: ct);
        await StepAsync("items (goods with barcodes, a service)", "accepted", client.Envelope("items", b + "-it", 1, new[]
        {
            OneCSampleClient.CatalogItem("it-001", "Сок яблочный 1 л", "cat-drinks", T(0), "Goods", OneCSampleClient.Barcode(1), OneCSampleClient.Barcode(2)),
            OneCSampleClient.CatalogItem("it-002", "Вода минеральная 0,5 л", "cat-drinks", T(0), "Goods", OneCSampleClient.Barcode(3)),
            OneCSampleClient.CatalogItem("it-003", "Доставка", "cat-services", T(0), "Service"),
        }), ok, ct: ct);
        await StepAsync("prices", "accepted", client.Envelope("prices", b + "-pr", 1, new[]
        {
            OneCSampleClient.Price("it-001", "pt-retail", 15_000m, T(0)), OneCSampleClient.Price("it-001", "pt-wholesale", 12_500m, T(0)),
            OneCSampleClient.Price("it-002", "pt-retail", 4_000m, T(0)), OneCSampleClient.Price("it-003", "pt-retail", 30_000m, T(0)),
        }), ok, ct: ct);
        await StepAsync("stock", "accepted", client.Envelope("stock", b + "-st", 1, new[]
        {
            OneCSampleClient.Stock("it-001", "wh-main", 120m, T(0), 5m), OneCSampleClient.Stock("it-001", "wh-shop", 12.5m, T(0)),
            OneCSampleClient.Stock("it-002", "wh-main", 480m, T(0)),
        }), ok, ct: ct);
    }

    /// <summary>Everything that goes wrong in real life (see the step names).</summary>
    public async Task MessyAsync(CancellationToken ct = default)
    {
        var p = "m" + _run + "-";
        var b = "messy-" + _run;
        static bool Is(SampleResponse r, string status) => r.Status == HttpStatusCode.OK && r.Get("status") == status;

        var unknown = client.Envelope("counterparties", b + "-1", 1, new object[]
        {
            new Dictionary<string, object?>
            {
                ["ref"] = p + "cp-1", ["tin"] = "301000001", ["name"] = "Unknown fields LLC", ["modified_at"] = "2026-11-01T10:00:00",
                ["ВидКонтрагента"] = "ЮрЛицо", ["custom_rating"] = new { score = 7, tags = new[] { "a", "b" } },
            },
            OneCSampleClient.Counterparty(p + "cp-2", "Second LLC", "301000002", T(60)),
        });
        unknown["extension_build"] = "2026.11.1"; // unknown envelope field
        await StepAsync("unknown fields in items and envelope", "accepted, unknown fields kept in extra and raw", unknown, r => Is(r, "accepted"), ct: ct);

        await StepAsync("items with errors (bad TIN, empty ref) next to a valid one", "partially_accepted: 1 accepted, 2 rejected (invalid_tin, empty_ref)",
            client.Envelope("counterparties", b + "-2", 1, new object[]
            {
                OneCSampleClient.Counterparty(p + "cp-3", "Valid LLC", "301000003", T(60)),
                OneCSampleClient.Counterparty(p + "cp-bad-tin", "Bad TIN LLC", "12345", T(60)),
                OneCSampleClient.Counterparty("", "No ref LLC", "301000004", T(60)),
            }),
            r => Is(r, "partially_accepted") && r.Items is [("accepted", _), ("rejected", "invalid_tin"), ("rejected", "empty_ref")], ct: ct);

        var fractional = OneCSampleClient.Document(p + "doc-frac", "Sales", p + "cp-1", "301000001", 100m, T(60));
        fractional["amount_total"] = "100.005";
        await StepAsync("fractional tiyin", "rejected: fractional_minor_units", client.Envelope("documents", b + "-3", 1, new object[] { fractional }),
            r => Is(r, "rejected") && r.Items is [("rejected", "fractional_minor_units")], ct: ct);

        await StepAsync("the same package again", "duplicate (nothing changed)", unknown, r => Is(r, "duplicate"), ct: ct);

        await StepAsync("late package with an older modified_at", "unchanged: older_than_stored",
            client.Envelope("counterparties", b + "-5", 1, new object[] { OneCSampleClient.Counterparty(p + "cp-2", "Old name", "301000002", T(0)) }),
            r => Is(r, "accepted") && r.Items is [("unchanged", "older_than_stored")], ct: ct);

        await StepAsync("document before its counterparty", "accepted (no foreign key to counterparties)",
            client.Envelope("documents", b + "-6", 1, new object[] { OneCSampleClient.Document(p + "doc-early", "Purchase", p + "cp-late", "301000009", 500m, T(60)) }),
            r => Is(r, "accepted"), ct: ct);
        await StepAsync("the counterparty arrives later", "accepted",
            client.Envelope("counterparties", b + "-7", 1, new object[] { OneCSampleClient.Counterparty(p + "cp-late", "Late LLC", "301000009", T(60)) }),
            r => Is(r, "accepted"), ct: ct);

        await StepAsync("documents to delete and unpost", "accepted",
            client.Envelope("documents", b + "-8", 1, new object[]
            {
                OneCSampleClient.Document(p + "doc-del", "Sales", p + "cp-1", "301000001", 10m, T(60)),
                OneCSampleClient.Document(p + "doc-unpost", "Sales", p + "cp-1", "301000001", 20m, T(60)),
            }), r => Is(r, "accepted"), ct: ct);
        await StepAsync("deletion (ref only), unposting, deletion of an unknown ref", "accepted, accepted, unchanged: unknown_ref_deleted",
            client.Envelope("documents", b + "-9", 1, new object[]
            {
                new Dictionary<string, object?> { ["ref"] = p + "doc-del", ["is_deleted"] = true, ["modified_at"] = At(T(120)) },
                OneCSampleClient.Document(p + "doc-unpost", "Sales", p + "cp-1", "301000001", 20m, T(120), posted: false),
                new Dictionary<string, object?> { ["ref"] = p + "doc-never-seen", ["is_deleted"] = true },
            }),
            r => Is(r, "accepted") && r.Items is [("accepted", _), ("accepted", _), ("unchanged", "unknown_ref_deleted")], ct: ct);

        // Full exports of payment orders: every payment order of the organization not in a completed export is marked deleted.
        var orders = Enumerable.Range(1, 4).Select(i => OneCSampleClient.PaymentOrder(p + $"po-{i}", "301000001", 100m * i, T(60))).ToList();
        await StepAsync("delta with 4 payment orders", "accepted", client.Envelope("payment_orders", b + "-10", 1, orders), r => Is(r, "accepted"), ct: ct);
        await StepAsync("truncated full export (no is_last_batch)", "accepted, nothing marked deleted",
            client.Envelope("payment_orders", b + "-full1", 1, orders.Take(2), "full", "F1-" + _run),
            r => Is(r, "accepted") && r.Int("marked_deleted") == 0, ct: ct);
        await StepAsync("full export, part 1 of 2", "accepted, waiting for the last batch",
            client.Envelope("payment_orders", b + "-full2", 1, orders.Take(2), "full", "F2-" + _run, false),
            r => Is(r, "accepted") && r.Int("marked_deleted") == 0, ct: ct);
        await StepAsync("full export, part 2 of 2 with is_last_batch (po-4 missing)", "accepted, po-4 and earlier orders not in the export marked deleted",
            client.Envelope("payment_orders", b + "-full2", 2, orders.Skip(2).Take(1), "full", "F2-" + _run, true),
            r => Is(r, "accepted") && r.Int("marked_deleted") >= 1, ct: ct);

        // Catalog: a price and stock before their item, wrong barcodes, full stock exports with and without is_last_batch.
        await StepAsync("price and stock before the item exists", "accepted (no foreign key to catalog_items)",
            client.Envelope("prices", b + "-c1", 1, new object[] { OneCSampleClient.Price(p + "it-late", "pt-retail", 9_900m, T(60)) }),
            r => Is(r, "accepted"), ct: ct);
        var badBarcode = OneCSampleClient.CatalogItem(p + "it-badbar", "Bad barcode", null, T(60), "Goods", "47800ABC");
        var unknownType = OneCSampleClient.CatalogItem(p + "it-type", "Unknown type", null, T(60), "Комплект");
        unknownType["ВесНетто"] = 0.5m;
        await StepAsync("the item arrives later; a wrong barcode; an unknown item type and field", "partially_accepted: accepted, rejected (invalid_barcode), accepted",
            client.Envelope("items", b + "-c2", 1, new object[] { OneCSampleClient.CatalogItem(p + "it-late", "Late item", null, T(60)), badBarcode, unknownType }),
            r => Is(r, "partially_accepted") && r.Items is [("accepted", _), ("rejected", "invalid_barcode"), ("accepted", _)], ct: ct);
        var fractionalPrice = OneCSampleClient.Price(p + "it-late", "pt-wholesale", 0m, T(60));
        fractionalPrice["price"] = "9900.005";
        await StepAsync("price with fractional tiyin", "rejected: fractional_minor_units",
            client.Envelope("prices", b + "-c3", 1, new object[] { fractionalPrice }), r => Is(r, "rejected") && r.Items is [("rejected", "fractional_minor_units")], ct: ct);

        var stock = Enumerable.Range(1, 4).Select(i => OneCSampleClient.Stock(p + $"it-{i}", "wh-main", 10m * i, T(60))).ToList();
        await StepAsync("delta with stock of 4 items", "accepted", client.Envelope("stock", b + "-c4", 1, stock), r => Is(r, "accepted"), ct: ct);
        await StepAsync("truncated full stock export (no is_last_batch)", "accepted, nothing removed",
            client.Envelope("stock", b + "-cfull1", 1, stock.Take(2), "full", "S1-" + _run),
            r => Is(r, "accepted") && r.Int("removed") == 0, ct: ct);
        await StepAsync("full stock export with is_last_batch (it-3, it-4 missing)", "accepted, rows not in the export removed and journaled",
            client.Envelope("stock", b + "-cfull2", 1, stock.Take(2), "full", "S2-" + _run, true),
            r => Is(r, "accepted") && r.Int("removed") >= 2, ct: ct);

        await StepAsync("unsupported schema_version", "HTTP 400 unsupported_schema_version",
            client.Envelope("counterparties", b + "-v", 1, new object[] { OneCSampleClient.Counterparty(p + "cp-v", "V2", null, T(60)) }, schemaVersion: 2),
            r => r.Status == HttpStatusCode.BadRequest && r.Get("error") == "unsupported_schema_version", ct: ct);
    }

    public sealed record CatalogMinuteResult(int Packages, int Items, int Accepted, int Unchanged, int Rejected, TimeSpan P50, TimeSpan P95, TimeSpan Max);

    /// <summary>
    /// What the 1C extension does for the Mailo bot: every <paramref name="interval"/> during <paramref name="duration"/>,
    /// a delta of <paramref name="positions"/> price changes and one of as many stock changes (the same items, new values).
    /// First a small catalog (items, a price type, a warehouse) is sent so that the changes refer to known items.
    /// </summary>
    public async Task<CatalogMinuteResult> CatalogMinuteAsync(TimeSpan duration, TimeSpan interval, int positions = 50, CancellationToken ct = default)
    {
        var p = "M" + _run + "-";
        var start = DateTimeOffset.UtcNow;
        await client.SendAsync(client.Envelope("price_types", p + "pt", 1, new[] { OneCSampleClient.PriceType(p + "pt", "Розничная", true, start) }), ct: ct);
        await client.SendAsync(client.Envelope("warehouses", p + "wh", 1, new[] { OneCSampleClient.Warehouse(p + "wh", "Магазин", start) }), ct: ct);
        await client.SendAsync(client.Envelope("items", p + "it", 1, Enumerable.Range(0, positions)
            .Select(i => OneCSampleClient.CatalogItem($"{p}it-{i}", $"Товар {i}", null, start, "Goods", OneCSampleClient.Barcode(i)))), ct: ct);

        var times = new List<TimeSpan>();
        int accepted = 0, unchanged = 0, rejected = 0, items = 0, round = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            round++;
            var at = start.AddSeconds(round);
            var prices = client.Envelope("prices", $"{p}pr-{round}", 1, Enumerable.Range(0, positions)
                .Select(i => OneCSampleClient.Price($"{p}it-{i}", p + "pt", 1000m + round * 10 + i, at)));
            var stock = client.Envelope("stock", $"{p}st-{round}", 1, Enumerable.Range(0, positions)
                .Select(i => OneCSampleClient.Stock($"{p}it-{i}", p + "wh", 100m - round + i * 0.5m, at)));
            foreach (var envelope in new[] { prices, stock })
            {
                var r = await client.SendAsync(envelope, ct: ct);
                if (r.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Catalog package refused: HTTP {(int)r.Status} {r.Get("error")}");
                times.Add(r.Elapsed);
                accepted += r.Int("accepted");
                unchanged += r.Int("unchanged");
                rejected += r.Int("rejected");
                items += positions;
            }
            var next = interval * round - watch.Elapsed;
            if (next > TimeSpan.Zero && watch.Elapsed + next < duration) await Task.Delay(next, ct);
            else if (watch.Elapsed + next >= duration) break;
        }
        times.Sort();
        TimeSpan Pct(double q) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(q * times.Count) - 1)];
        return new CatalogMinuteResult(times.Count, items, accepted, unchanged, rejected, Pct(0.5), Pct(0.95), times[^1]);
    }

    public sealed record LargeResult(int Records, int Packages, TimeSpan First, TimeSpan Duplicates, TimeSpan Unchanged, int Accepted, int Rejected,
        int DuplicateAnswers, int UnchangedItems);

    /// <summary>
    /// 20 000 records in packages of 500: 4 000 counterparties, 10 000 documents with 2 lines, 6 000 bank documents with a
    /// settlement. Then the same packages again (duplicates) and the same records under new batch ids (all unchanged).
    /// </summary>
    public async Task<LargeResult> LargeAsync(int records = 20_000, int packageSize = 500, CancellationToken ct = default)
    {
        var p = "L" + _run + "-";
        var items = new List<(string Entity, object Item)>(records);
        var counterparties = records / 5;
        var documents = records / 2;
        var bank = records - counterparties - documents;
        for (var i = 0; i < counterparties; i++)
            items.Add(("counterparties", OneCSampleClient.Counterparty($"{p}cp-{i}", $"Large counterparty {i}", OneCSampleClient.Tin(i), T(0))));
        for (var i = 0; i < documents; i++)
            items.Add(("documents", OneCSampleClient.Document($"{p}doc-{i}", i % 2 == 0 ? "Sales" : "Purchase", $"{p}cp-{i % counterparties}",
                OneCSampleClient.Tin(i % counterparties), 1000m + i, T(0))));
        for (var i = 0; i < bank; i++)
            items.Add(("bank_documents", OneCSampleClient.BankDocument($"{p}bd-{i}", i % 2 == 0 ? "Receipt" : "Payment", $"{p}cp-{i % counterparties}",
                OneCSampleClient.Tin(i % counterparties), 1000m + i, T(0), $"{p}doc-{i}")));
        return await SendLargeAsync(items, "large-" + _run, packageSize, ct);
    }

    /// <summary>
    /// 20 000 catalog records in packages of 500: 10 000 items (with a barcode), 5 000 prices and 5 000 stock rows, then the
    /// same packages again and the same records under new batch ids.
    /// </summary>
    public async Task<LargeResult> LargeCatalogAsync(int records = 20_000, int packageSize = 500, CancellationToken ct = default)
    {
        var p = "LC" + _run + "-";
        var itemCount = records / 2;
        var prices = records / 4;
        var stock = records - itemCount - prices;
        var items = new List<(string Entity, object Item)>(records);
        for (var i = 0; i < itemCount; i++)
            items.Add(("items", OneCSampleClient.CatalogItem($"{p}it-{i}", $"Товар номер {i}", $"{p}cat-{i % 50}", T(0), i % 10 == 0 ? "Service" : "Goods",
                OneCSampleClient.Barcode(i))));
        for (var i = 0; i < prices; i++) items.Add(("prices", OneCSampleClient.Price($"{p}it-{i}", p + "pt", 1000m + i, T(0))));
        for (var i = 0; i < stock; i++) items.Add(("stock", OneCSampleClient.Stock($"{p}it-{i}", p + "wh", i % 100 + 0.5m, T(0))));
        return await SendLargeAsync(items, "large-catalog-" + _run, packageSize, ct);
    }

    private async Task<LargeResult> SendLargeAsync(List<(string Entity, object Item)> items, string batch0, int packageSize, CancellationToken ct)
    {
        var records = items.Count;
        var packages = items.GroupBy(x => x.Entity)
            .SelectMany(g => g.Chunk(packageSize).Select((chunk, n) => (Entity: g.Key, Sequence: n + 1, Items: chunk.Select(c => c.Item).ToList())))
            .ToList();
        byte[] Body(string batch, (string Entity, int Sequence, List<object> Items) x) =>
            JsonSerializer.SerializeToUtf8Bytes(client.Envelope(x.Entity, $"{batch}-{x.Entity}", x.Sequence, x.Items));
        var first = packages.Select(x => Body(batch0, x)).ToList();
        var again = packages.Select(x => Body(batch0 + "-again", x)).ToList();

        int accepted = 0, rejected = 0, duplicates = 0, unchanged = 0;
        var watch = Stopwatch.StartNew();
        foreach (var body in first)
        {
            var r = await client.SendRawAsync(body, ct: ct);
            if (r.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Large package refused: HTTP {(int)r.Status} {r.Get("error")}");
            accepted += r.Int("accepted");
            rejected += r.Int("rejected");
        }
        var firstTime = watch.Elapsed;
        watch.Restart();
        foreach (var body in first)
            if ((await client.SendRawAsync(body, ct: ct)).Get("status") == "duplicate") duplicates++;
        var duplicateTime = watch.Elapsed;
        watch.Restart();
        foreach (var body in again) unchanged += (await client.SendRawAsync(body, ct: ct)).Int("unchanged");
        var unchangedTime = watch.Elapsed;
        return new LargeResult(records, packages.Count, firstTime, duplicateTime, unchangedTime, accepted, rejected, duplicates, unchanged);
    }

    private static string At(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
