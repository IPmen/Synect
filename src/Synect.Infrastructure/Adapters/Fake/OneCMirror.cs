using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Banking;
using Synect.Domain.Edo;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>Synthetic 1C data of one organization, as items of the export contract per entity.</summary>
public sealed class OneCMirrorData
{
    public List<Dictionary<string, object?>> Counterparties { get; } = [];
    public List<Dictionary<string, object?>> Contracts { get; } = [];
    public List<Dictionary<string, object?>> BankAccounts { get; } = [];
    public List<Dictionary<string, object?>> Documents { get; } = [];
    public List<Dictionary<string, object?>> BankDocuments { get; } = [];
    public List<Dictionary<string, object?>> PaymentOrders { get; } = [];
    public List<Dictionary<string, object?>> SettlementBalances { get; } = [];
    public List<Dictionary<string, object?>> BankAccountBalances { get; } = [];

    /// <summary>Bank closing balance per account number on the balance date (minor units), as read from the bank data.</summary>
    public Dictionary<string, long> BankBalances { get; } = new(StringComparer.Ordinal);

    public DateOnly AsOf { get; init; }

    public IEnumerable<(string Entity, List<Dictionary<string, object?>> Items)> Entities =>
    [
        ("counterparties", Counterparties), ("contracts", Contracts), ("bank_accounts", BankAccounts), ("documents", Documents),
        ("bank_documents", BankDocuments), ("payment_orders", PaymentOrders), ("settlement_balances", SettlementBalances),
        ("bank_account_balances", BankAccountBalances),
    ];
}

public sealed record OneCMirrorSendResult(int Packages, int Accepted, int Unchanged, int Rejected);

/// <summary>
/// Builds 1C data that mirrors what Synect already holds for an organization (development and tests only): every
/// error-free bank operation becomes a 1C bank document, every Didox document of the given statuses and types a 1C sales
/// or purchase document, with counterparties, contracts, bank accounts, payment orders and balances to match. A mirror
/// without changes compares as "everything found"; planted differences (see <see cref="Accounting1CGoldenSet"/> and
/// <see cref="PlantGenericDifferences"/>) then have known answers.
/// </summary>
public static class OneCMirror
{
    public const string ReceivablesAccount = "4010";
    public const string PayablesAccount = "6010";

    public static string Money(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string D(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Modified(DateOnly date) => D(date) + "T12:00:00";

    /// <summary>Reads the organization's data in the current tenant context.</summary>
    public static Task<OneCMirrorData> BuildAsync(ISynectDbContext db, DateOnly asOf, IReadOnlyCollection<string> statuses,
        IReadOnlyCollection<string> docTypes, TimeSpan localOffset, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var data = new OneCMirrorData { AsOf = asOf };
            var statusList = statuses.ToList();
            var typeList = docTypes.ToList();
            var accounts = await db.BankAccounts.AsNoTracking().OrderBy(a => a.AccountNumber).ToListAsync(token);
            var accountById = accounts.ToDictionary(a => a.Id);
            foreach (var a in accounts)
            {
                data.BankAccounts.Add(new()
                {
                    ["ref"] = "ba-" + a.AccountNumber, ["number"] = a.AccountNumber, ["bank_code"] = a.Mfo, ["bank_name"] = "Mirror bank",
                    ["currency"] = a.Currency, ["gl_account_code"] = a.Currency == "UZS" ? "5110" : "5210", ["is_active"] = true,
                    ["modified_at"] = "2026-01-01T12:00:00",
                });
                var balance = await db.BankDailyBalances.AsNoTracking().Where(b => b.BankAccountId == a.Id && b.Date <= asOf)
                    .OrderByDescending(b => b.Date).Select(b => (long?)b.ClosingBalance).FirstOrDefaultAsync(token);
                if (balance is { } closing)
                {
                    data.BankBalances[a.AccountNumber] = closing;
                    data.BankAccountBalances.Add(new() { ["as_of_date"] = D(asOf), ["bank_account_ref"] = "ba-" + a.AccountNumber, ["balance"] = Money(closing) });
                }
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var operations = await db.BankTransactions.AsNoTracking().Where(t => !t.HasError && t.PostingDate <= asOf)
                .OrderBy(t => t.PostingDate).ThenBy(t => t.Id).ToListAsync(token);
            foreach (var t in operations)
            {
                var account = accountById[t.BankAccountId];
                var inn = string.IsNullOrWhiteSpace(t.CounterpartyInn) ? null : t.CounterpartyInn;
                if (inn is not null) names.TryAdd(inn, t.CounterpartyName ?? "Counterparty " + inn);
                var id = t.Id.ToString("N");
                data.BankDocuments.Add(new()
                {
                    ["ref"] = "bd-" + id, ["direction"] = t.Direction == TransactionDirection.Incoming ? "Receipt" : "Payment",
                    ["number"] = "B-" + id[^8..], ["date"] = D(t.PostingDate), ["posted"] = true, ["bank_account_ref"] = "ba-" + account.AccountNumber,
                    ["bank_account_number"] = account.AccountNumber, ["counterparty_ref"] = inn is null ? null : "cp-" + inn, ["counterparty_tin"] = inn,
                    ["amount"] = Money(t.Amount), ["currency"] = t.Currency, ["purpose"] = t.Purpose, ["bank_document_number"] = t.DocumentNumber,
                    ["bank_document_date"] = D(t.PostingDate), ["modified_at"] = Modified(t.PostingDate),
                });
                if (t.Direction == TransactionDirection.Outgoing)
                    data.PaymentOrders.Add(new()
                    {
                        ["ref"] = "po-" + id, ["number"] = "P-" + id[^8..], ["date"] = D(t.PostingDate), ["posted"] = true,
                        ["bank_account_ref"] = "ba-" + account.AccountNumber, ["counterparty_tin"] = inn, ["amount"] = Money(t.Amount),
                        ["currency"] = t.Currency, ["purpose"] = t.Purpose, ["bank_status"] = "Исполнено", ["modified_at"] = Modified(t.PostingDate),
                    });
            }

            var documents = (await db.EdoDocuments.AsNoTracking().Where(d => statusList.Contains(d.Status) && typeList.Contains(d.DocType)).ToListAsync(token))
                .Select(d => (Doc: d, Date: d.DocumentDate ?? (d.SourceCreatedAt is { } at ? DateOnly.FromDateTime(at.ToOffset(localOffset).DateTime) : asOf)))
                .Where(x => x.Date <= asOf).OrderBy(x => x.Date).ThenBy(x => x.Doc.ExternalId, StringComparer.Ordinal).ToList();
            var contracts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (d, date) in documents)
            {
                var inn = string.IsNullOrWhiteSpace(d.CounterpartyInn) ? null : d.CounterpartyInn;
                if (inn is not null) names.TryAdd(inn, d.CounterpartyName ?? "Counterparty " + inn);
                var total = d.TotalDeliverySumWithVat > 0 ? d.TotalDeliverySumWithVat : d.TotalSum;
                string? contractRef = null;
                if (inn is not null && !string.IsNullOrWhiteSpace(d.ContractNumber))
                {
                    contractRef = Cut($"ct-{inn}-{d.ContractNumber}");
                    if (contracts.Add(contractRef))
                        data.Contracts.Add(new()
                        {
                            ["ref"] = contractRef, ["counterparty_ref"] = "cp-" + inn, ["number"] = d.ContractNumber,
                            ["date"] = d.ContractDate is { } cd ? D(cd) : null, ["currency"] = d.Currency, ["modified_at"] = "2026-01-01T12:00:00",
                        });
                }
                data.Documents.Add(new()
                {
                    ["ref"] = Cut("doc-" + d.ExternalId), ["doc_kind"] = d.Owner == EdoDocumentOwner.Outgoing ? "Sales" : "Purchase",
                    ["doc_type"] = d.Owner == EdoDocumentOwner.Outgoing ? "РеализацияТоваровУслуг" : "ПоступлениеТоваровУслуг",
                    ["number"] = d.Number ?? d.ExternalId, ["date"] = D(date), ["posted"] = true, ["counterparty_ref"] = inn is null ? null : "cp-" + inn,
                    ["counterparty_tin"] = inn, ["contract_ref"] = contractRef, ["contract_number"] = d.ContractNumber,
                    ["contract_date"] = d.ContractDate is { } c ? D(c) : null, ["currency"] = d.Currency, ["amount_total"] = Money(total),
                    ["vat_amount"] = Money(d.TotalVatSum), ["esf_number"] = d.Number, ["esf_date"] = D(date), ["esf_id"] = d.ExternalId,
                    ["esf_status"] = d.Status, ["modified_at"] = Modified(date),
                    ["lines"] = new[] { new Dictionary<string, object?> { ["line_no"] = 1, ["name"] = "Mirror item", ["quantity"] = 1, ["amount"] = Money(total) } },
                });
            }

            foreach (var (inn, name) in names.OrderBy(n => n.Key, StringComparer.Ordinal))
                data.Counterparties.Add(new() { ["ref"] = "cp-" + inn, ["tin"] = inn, ["name"] = name, ["modified_at"] = "2026-01-01T12:00:00" });
            RecalculateSettlements(data);
            return data;
        }, ct);

    private static string Cut(string value) => value.Length <= 64 ? value : value[..64];

    private static long Minor(object? amount) =>
        (long)(decimal.Parse(Convert.ToString(amount, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture) * 100m);

    /// <summary>
    /// Settlement balances that follow from the 1C documents and bank documents of the mirror (posted, not deleted):
    /// customers on 4010 (sales minus receipts), suppliers on 6010 (purchases minus payments). Call again after changing items.
    /// </summary>
    public static void RecalculateSettlements(OneCMirrorData data)
    {
        static bool Counts(Dictionary<string, object?> item) => item.GetValueOrDefault("posted") is true && item.GetValueOrDefault("is_deleted") is not true;
        var totals = new Dictionary<(string Ref, string Tin, string Currency), (long Sales, long Receipts, long Purchases, long Payments)>();
        void Add(Dictionary<string, object?> item, string amountField, Func<(long, long, long, long), long, (long, long, long, long)> apply)
        {
            if (!Counts(item) || item.GetValueOrDefault("counterparty_ref") is not string reference) return;
            var key = (reference, (string?)item.GetValueOrDefault("counterparty_tin") ?? "", (string)item["currency"]!);
            totals[key] = apply(totals.GetValueOrDefault(key), Minor(item[amountField]));
        }
        foreach (var d in data.Documents)
            Add(d, "amount_total", (string)d["doc_kind"]! == "Sales" ? (t, a) => (t.Item1 + a, t.Item2, t.Item3, t.Item4) : (t, a) => (t.Item1, t.Item2, t.Item3 + a, t.Item4));
        foreach (var b in data.BankDocuments)
            Add(b, "amount", (string)b["direction"]! == "Receipt" ? (t, a) => (t.Item1, t.Item2 + a, t.Item3, t.Item4) : (t, a) => (t.Item1, t.Item2, t.Item3, t.Item4 + a));

        data.SettlementBalances.Clear();
        foreach (var (key, t) in totals.OrderBy(x => x.Key.Ref, StringComparer.Ordinal).ThenBy(x => x.Key.Currency, StringComparer.Ordinal))
        {
            void Row(string account, long net, long debitTurnover, long creditTurnover)
            {
                if (debitTurnover == 0 && creditTurnover == 0) return;
                data.SettlementBalances.Add(new()
                {
                    ["as_of_date"] = D(data.AsOf), ["counterparty_ref"] = key.Ref, ["counterparty_tin"] = key.Tin.Length == 0 ? null : key.Tin,
                    ["gl_account_code"] = account, ["currency"] = key.Currency, ["balance_debit"] = Money(Math.Max(net, 0)),
                    ["balance_credit"] = Money(Math.Max(-net, 0)), ["turnover_debit"] = Money(debitTurnover), ["turnover_credit"] = Money(creditTurnover),
                });
            }
            Row(ReceivablesAccount, t.Sales - t.Receipts, t.Sales, t.Receipts);
            Row(PayablesAccount, t.Payments - t.Purchases, t.Payments, t.Purchases);
        }
    }

    /// <summary>
    /// Plants differences into a mirror by fixed rules, for demo data without a hand-made answer list: every 20th bank
    /// document is left out, every 33rd gets +1.00, every 50th is unposted; every 15th document is left out, every 25th gets
    /// +100.00. Returns what was planted.
    /// </summary>
    public static IReadOnlyDictionary<string, int> PlantGenericDifferences(OneCMirrorData data)
    {
        var planted = new Dictionary<string, int> { ["payments_missing_in_1c"] = 0, ["payments_amount_differs"] = 0, ["payments_not_posted"] = 0, ["documents_missing_in_1c"] = 0, ["documents_amount_differs"] = 0 };
        for (var i = data.BankDocuments.Count - 1; i >= 0; i--)
        {
            var n = i + 1;
            if (n % 20 == 0) { data.BankDocuments.RemoveAt(i); planted["payments_missing_in_1c"]++; }
            else if (n % 33 == 0) { data.BankDocuments[i]["amount"] = Money(Minor(data.BankDocuments[i]["amount"]) + 100); planted["payments_amount_differs"]++; }
            else if (n % 50 == 0) { data.BankDocuments[i]["posted"] = false; planted["payments_not_posted"]++; }
        }
        for (var i = data.Documents.Count - 1; i >= 0; i--)
        {
            var n = i + 1;
            if (n % 15 == 0) { data.Documents.RemoveAt(i); planted["documents_missing_in_1c"]++; }
            else if (n % 25 == 0)
            {
                var amount = Money(Minor(data.Documents[i]["amount_total"]) + 100_00);
                data.Documents[i]["amount_total"] = amount;
                ((Dictionary<string, object?>[])data.Documents[i]["lines"]!)[0]["amount"] = amount;
                planted["documents_amount_differs"]++;
            }
        }
        RecalculateSettlements(data);
        return planted;
    }

    /// <summary>Sends the mirror through the receiver, one delta batch id per entity, packages of <paramref name="packageSize"/>.</summary>
    public static async Task<OneCMirrorSendResult> SendAsync(OneCSampleClient client, OneCMirrorData data, string batchPrefix, int packageSize = 500,
        CancellationToken ct = default)
    {
        int packages = 0, accepted = 0, unchanged = 0, rejected = 0;
        foreach (var (entity, items) in data.Entities)
        {
            var sequence = 0;
            foreach (var chunk in items.Chunk(packageSize))
            {
                var r = await client.SendAsync(client.Envelope(entity, $"{batchPrefix}-{entity}", ++sequence, chunk), gzip: true, ct);
                if (r.Status != HttpStatusCode.OK)
                    throw new InvalidOperationException($"Mirror package {entity}#{sequence} refused: HTTP {(int)r.Status} {r.Get("error")}");
                packages++;
                accepted += r.Int("accepted");
                unchanged += r.Int("unchanged");
                rejected += r.Int("rejected");
            }
        }
        return new OneCMirrorSendResult(packages, accepted, unchanged, rejected);
    }
}

/// <summary>A planted difference of the 1C golden set and where it must show up.</summary>
public sealed record Accounting1CGoldenCase(string Case, string Tool, string Kind, string Key);

/// <summary>
/// 1C data for the reconciliation golden set (<see cref="ReconciliationGoldenSet"/>): a mirror of its 23 bank operations
/// and 24 comparable Didox documents with planted differences whose right answers are listed here. Used by tests and by
/// `accounting-eval`.
/// </summary>
public static class Accounting1CGoldenSet
{
    public static readonly DateOnly AsOf = new(2026, 10, 31);
    public static readonly DateOnly From = new(2026, 10, 1);

    /// <summary>The currency account whose 1C balance is 1 000.00 higher than the bank's.</summary>
    public const long UsdBalanceDifference = 1_000_00;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static Dictionary<string, object?> Payment(OneCMirrorData data, int day, long minor, string? tin) =>
        data.BankDocuments.Single(b => (string)b["date"]! == Oct(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                       && (string)b["amount"]! == OneCMirror.Money(minor) && (string?)b["counterparty_tin"] == tin);

    private static Dictionary<string, object?> Document(OneCMirrorData data, string didoxId) =>
        data.Documents.Single(d => (string?)d["esf_id"] == didoxId);

    /// <summary>Plants the differences (see the comments) and recalculates the settlement balances.</summary>
    public static void Apply(OneCMirrorData data)
    {
        const string A = ReconciliationGoldenSet.A, B = ReconciliationGoldenSet.B, C = ReconciliationGoldenSet.C, D = ReconciliationGoldenSet.D,
            H = ReconciliationGoldenSet.H;

        // Bank against 1C.
        data.BankDocuments.Remove(Payment(data, 20, 333_333_33, C));               // p4: not entered in 1C
        Payment(data, 10, 500_000_00, A)["amount"] = "505000.00";                   // p2: another amount in 1C (+5 000.00)
        Payment(data, 10, 555_000_00, H)["posted"] = false;                          // p17: entered but not posted
        Payment(data, 19, 450_000_00, B)["date"] = "2026-10-20";                    // p8b: next day in 1C, still the same payment
        data.BankDocuments.Add(new()                                                 // in 1C only
        {
            ["ref"] = "bd-extra-1", ["direction"] = "Payment", ["number"] = "B-EXTRA-1", ["date"] = "2026-10-22", ["posted"] = true,
            ["counterparty_ref"] = "cp-" + D, ["counterparty_tin"] = D, ["amount"] = "150000.00", ["currency"] = "UZS",
            ["purpose"] = "Оплата поставщику (нет в выписке)", ["modified_at"] = "2026-10-22T12:00:00",
        });

        // Didox against 1C.
        data.Documents.Remove(Document(data, "d7c"));                                // F-3: not entered in 1C
        SetTotal(Document(data, "d9"), "110000.00");                                 // 47: another amount in 1C (+10 000.00)
        Document(data, "d16h")["posted"] = false;                                    // H-20: not posted
        foreach (var id in new[] { "d2", "d3", "d15e" }) Document(data, id)["esf_id"] = null;   // found by number and INN
        var act = Document(data, "d20");                                             // АКТ-5: no ESF data in 1C, found by amount, INN and date
        act["esf_id"] = null;
        act["esf_number"] = null;
        act["esf_date"] = null;
        act["number"] = "000123";
        data.Documents.Add(new()                                                     // in 1C only, without ESF
        {
            ["ref"] = "doc-extra-1", ["doc_kind"] = "Sales", ["doc_type"] = "РеализацияТоваровУслуг", ["number"] = "H-99", ["date"] = "2026-10-18",
            ["posted"] = true, ["counterparty_ref"] = "cp-" + H, ["counterparty_tin"] = H, ["currency"] = "UZS", ["amount_total"] = "70000.00",
            ["modified_at"] = "2026-10-18T12:00:00",
        });
        data.Documents.Add(new()                                                     // marked deleted in 1C: never compared
        {
            ["ref"] = "doc-deleted-1", ["doc_kind"] = "Sales", ["number"] = "A-DEL", ["date"] = "2026-10-12", ["posted"] = false, ["is_deleted"] = true,
            ["counterparty_ref"] = "cp-" + A, ["counterparty_tin"] = A, ["currency"] = "UZS", ["amount_total"] = "10000.00",
            ["modified_at"] = "2026-10-12T12:00:00",
        });

        // A payment order prepared in 1C and not sent to the bank.
        data.PaymentOrders.Add(new()
        {
            ["ref"] = "po-extra-1", ["number"] = "P-EXTRA-1", ["date"] = "2026-10-22", ["posted"] = false, ["counterparty_tin"] = D,
            ["amount"] = "150000.00", ["currency"] = "UZS", ["purpose"] = "Оплата поставщику", ["bank_status"] = "Подготовлено",
            ["modified_at"] = "2026-10-22T12:00:00",
        });

        // The balance of the currency account differs in 1C.
        var usd = data.BankAccounts.Single(a => (string)a["currency"]! == "USD");
        var usdNumber = (string)usd["number"]!;
        data.BankAccountBalances.Single(b => (string)b["bank_account_ref"]! == "ba-" + usdNumber)["balance"] =
            OneCMirror.Money(data.BankBalances[usdNumber] + UsdBalanceDifference);

        OneCMirror.RecalculateSettlements(data);
    }

    private static void SetTotal(Dictionary<string, object?> document, string amount)
    {
        document["amount_total"] = amount;
        ((Dictionary<string, object?>[])document["lines"]!)[0]["amount"] = amount;
    }

    /// <summary>Right answers for October 2026: tool, kind of difference and the key of the row (1C ref, Didox id or bank amount/INN).</summary>
    public static IReadOnlyList<Accounting1CGoldenCase> Expected { get; } =
    [
        new("p4 payment not entered in 1C", "compare_bank_with_1c", "MissingIn1C", "33333333|" + ReconciliationGoldenSet.C),
        new("payment only in 1C", "compare_bank_with_1c", "MissingInSource", "bd-extra-1"),
        new("p2 payment with another amount in 1C", "compare_bank_with_1c", "AmountDiffers", "50000000|" + ReconciliationGoldenSet.A),
        new("p17 payment not posted in 1C", "compare_bank_with_1c", "NotPostedIn1C", "55500000|" + ReconciliationGoldenSet.H),
        new("F-3 document not entered in 1C", "compare_didox_with_1c", "MissingIn1C", "d7c"),
        new("document only in 1C", "compare_didox_with_1c", "MissingInSource", "doc-extra-1"),
        new("47 document with another amount in 1C", "compare_didox_with_1c", "AmountDiffers", "d9"),
        new("H-20 document not posted in 1C", "compare_didox_with_1c", "NotPostedIn1C", "d16h"),
    ];

    /// <summary>Pairs without a difference in October: 23 bank operations minus 3, and 24 comparable Didox documents minus 3.</summary>
    public const int MatchedPayments = 20;
    public const int MatchedDocuments = 21;
}
