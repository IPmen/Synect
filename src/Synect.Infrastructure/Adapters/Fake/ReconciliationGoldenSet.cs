using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Reconciliation;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>An expected automatic match of the golden set (payment and document keys); Type null = any type.</summary>
public sealed record GoldenMatch(string Case, ReconciliationMatchType? Type, string[] Payments, string[] Documents);

/// <summary>A case where the right answer is "no automatic match" (ambiguous, excluded or wrong direction).</summary>
public sealed record GoldenNoMatch(string Case, string[] Payments, string[] Documents, string Why);

/// <param name="ProducedMatches">Every produced match as "type confidence payments &lt;-&gt; documents".</param>
public sealed record GoldenEvaluation(
    int Expected, int Produced, int Correct, double Precision, double Recall, IReadOnlyList<string> Errors, IReadOnlyList<string> ProducedMatches);

/// <summary>
/// Deterministic reconciliation data set with known right answers, loaded through the fake bank and Didox adapters and
/// the real sync services. Covers purposes in Russian, Uzbek Latin and Uzbek Cyrillic, matches by document number, by
/// contract number and by unique amount, partial payments, one payment for several documents, several payments for one
/// document, overpayment, double payment, a payment to another account, a payment without an INN, a payment in another
/// currency, a document in an uncounted status, equal document numbers of different counterparties, equal amounts of
/// different counterparties, ambiguous candidates, opposite directions and a signed act without a payment.
/// </summary>
public static class ReconciliationGoldenSet
{
    public const string TenantName = "Reconciliation golden set";
    public static readonly DateOnly BankFrom = new(2026, 9, 28);
    public static readonly DateOnly BankTo = new(2026, 10, 31);

    public const string A = "301111111", B = "302222222", C = "303333333", D = "304444444", E = "305555555", F = "306666666", G = "307777777", H = "308888888";
    public const string DSellerAccount = "20208000900000000001";
    public const string DOtherAccount = "20208000900000000099";
    public const string BAccount = "20208000800000000007";

    private static DateOnly Oct(int day) => new(2026, 10, day);

    public static IReadOnlyList<FakeBankOperation> Operations { get; } =
    [
        // 1. Russian purpose with the document number.
        new() { Key = "p1", Day = Oct(5), Dir = 1, Amount = 1_200_000_00, Inn = A, Name = "ООО «АЛЬФА ТРЕЙД»", Purpose = "Оплата по счет-фактуре №СФ-101 от 01.10.2026 за товар, в т.ч. НДС 12%" },
        // 2. Uzbek Latin, number before the keyword.
        new() { Key = "p2", Day = Oct(10), Dir = 1, Amount = 500_000_00, Inn = A, Name = "ALFA TREYD MCHJ", Purpose = "2026 yil 3 oktyabrdagi 202-sonli hisob-faktura bo'yicha to'lov, QQS bilan" },
        // 3. Uzbek Cyrillic.
        new() { Key = "p3", Day = Oct(6), Dir = 1, Amount = 750_000_00, Inn = C, Name = "ГАММА СЕРВИС МЧЖ", Purpose = "45-сонли ҳисоб-фактура бўйича тўлов" },
        // 4. No number, the only document of that amount.
        new() { Key = "p4", Day = Oct(20), Dir = 1, Amount = 333_333_33, Inn = C, Name = "ГАММА СЕРВИС МЧЖ", Purpose = "Оплата за услуги" },
        // 5. Contract number only (we buy), payee account equals sellerAccount.
        new() { Key = "p5", Day = Oct(8), Dir = 2, Amount = 2_000_000_00, Inn = B, Name = "BETA QURILISH MCHJ", Account = BAccount, Purpose = "Оплата по договору №15/2026 за строительные материалы" },
        // 6. Two partial payments of one invoice.
        new() { Key = "p6a", Day = Oct(10), Dir = 1, Amount = 1_000_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Частичная оплата по с/ф №СФ-103" },
        new() { Key = "p6b", Day = Oct(25), Dir = 1, Amount = 1_000_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Оплата по сф СФ-103 (2-я часть)" },
        // 7. One payment for two of three documents, no numbers.
        new() { Key = "p7", Day = Oct(12), Dir = 1, Amount = 1_000_000_00, Inn = F, Name = "FOKUS SAVDO", Purpose = "Оплата за товар" },
        // 7b. One payment naming two documents.
        new() { Key = "p7b", Day = Oct(14), Dir = 1, Amount = 500_000_00, Inn = G, Name = "GLOBUS LLC", Purpose = "Оплата по счетам-фактурам G-10 и G-11" },
        // 8. Two payments for one act, numbers in both.
        new() { Key = "p8a", Day = Oct(12), Dir = 2, Amount = 450_000_00, Inn = B, Name = "BETA QURILISH MCHJ", Account = BAccount, Purpose = "Оплата по акту №Б-80, часть 1" },
        new() { Key = "p8b", Day = Oct(19), Dir = 2, Amount = 450_000_00, Inn = B, Name = "BETA QURILISH MCHJ", Account = BAccount, Purpose = "Оплата по акту №Б-80, часть 2" },
        // 9. Overpayment.
        new() { Key = "p9", Day = Oct(15), Dir = 1, Amount = 120_000_00, Inn = C, Name = "ГАММА СЕРВИС", Purpose = "Оплата по с/ф 47" },
        // 10. Double payment of one invoice.
        new() { Key = "p10a", Day = Oct(11), Dir = 1, Amount = 250_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Оплата по счет-фактуре СФ-104" },
        new() { Key = "p10b", Day = Oct(12), Dir = 1, Amount = 250_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Оплата по счет-фактуре СФ-104" },
        // 11. Payment to an account other than the document's sellerAccount.
        new() { Key = "p11", Day = Oct(7), Dir = 2, Amount = 800_000_00, Inn = D, Name = "DELTA TEXNIKA", Account = DOtherAccount, Purpose = "Оплата по счету-фактуре D-9" },
        // 12. Without INN (never matched automatically).
        new() { Key = "p12", Day = Oct(14), Dir = 1, Amount = 1_200_000_00, Inn = "", Name = "НАЛИЧНЫЕ", Purpose = "Взнос наличных, оплата по СФ-101" },
        // 13. Payment in USD (excluded: documents are in UZS).
        new() { Key = "p13", AccountIndex = 1, Day = Oct(15), Dir = 1, Amount = 50_000_00, Inn = A, Name = "ALFA TRADE", Purpose = "Payment for invoice SF-105" },
        // 14. Document in an uncounted status (draft): the payment stays unmatched.
        new() { Key = "p14", Day = Oct(16), Dir = 1, Amount = 77_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Оплата по СФ-106" },
        // 15. Same document number "100" at two counterparties.
        new() { Key = "p15", Day = Oct(6), Dir = 1, Amount = 410_000_00, Inn = E, Name = "EKO PRODUKT", Purpose = "Оплата по с/ф №100" },
        // 16. Equal document amounts at two counterparties.
        new() { Key = "p16", Day = Oct(9), Dir = 1, Amount = 999_000_00, Inn = G, Name = "GLOBUS LLC", Purpose = "Оплата за поставку" },
        // 17. Two documents of the same amount: ambiguous.
        new() { Key = "p17", Day = Oct(10), Dir = 1, Amount = 555_000_00, Inn = H, Name = "HAMKOR", Purpose = "Оплата" },
        // 18. Outgoing payment naming our outgoing document: opposite directions.
        new() { Key = "p18", Day = Oct(8), Dir = 2, Amount = 640_000_00, Inn = G, Name = "GLOBUS LLC", Purpose = "Возврат по G-30" },
        // 19. Payment for a contract document type (not a paid type).
        new() { Key = "p19", Day = Oct(20), Dir = 1, Amount = 1_500_000_00, Inn = A, Name = "ООО АЛЬФА ТРЕЙД", Purpose = "Оплата по договору Д-1" },
    ];

    public static IReadOnlyList<FakeDidoxDocument> Documents()
    {
        static FakeDidoxDocument Doc(string id, int owner, string type, string number, DateOnly date, string inn, decimal total, int status = 3,
            string? contract = null, string? seller = null) => new()
        {
            DocId = id, DocType = type, Owner = owner, Status = status, Name = number, DocDate = date, PartnerTin = inn,
            PartnerCompany = "PARTNER " + inn, ContractNumber = contract, ContractDate = contract is null ? null : new DateOnly(2026, 1, 15),
            TotalSum = total, TotalVatSum = Math.Round(total * 12m / 112m, 2),
            Created = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(5)),
            Updated = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(5)), SellerAccount = seller,
        };
        return
        [
            Doc("d1", 1, "002", "СФ-101", Oct(1), A, 1_200_000m),
            Doc("d2", 1, "002", "202", Oct(3), A, 500_000m),
            Doc("d3", 1, "002", "45", Oct(2), C, 750_000m),
            Doc("d4", 1, "002", "46", Oct(6), C, 333_333.33m),
            Doc("d5", 0, "002", "Б-77", Oct(4), B, 2_000_000m, contract: "15/2026", seller: BAccount),
            Doc("d6", 1, "002", "СФ-103", Oct(7), A, 3_000_000m),
            Doc("d7a", 1, "002", "F-1", Oct(1), F, 400_000m),
            Doc("d7b", 1, "002", "F-2", Oct(5), F, 600_000m),
            Doc("d7c", 1, "002", "F-3", Oct(9), F, 250_000m),
            Doc("d7d", 1, "002", "G-10", Oct(6), G, 300_000m),
            Doc("d7e", 1, "002", "G-11", Oct(7), G, 200_000m),
            Doc("d8", 0, "005", "Б-80", Oct(10), B, 900_000m, seller: BAccount),
            Doc("d9", 1, "002", "47", Oct(8), C, 100_000m),
            Doc("d10", 1, "002", "СФ-104", Oct(9), A, 250_000m),
            Doc("d11", 0, "002", "D-9", Oct(5), D, 800_000m, seller: DSellerAccount),
            Doc("d13", 1, "002", "SF-105", Oct(13), A, 50_000m),
            Doc("d14", 1, "002", "СФ-106", Oct(14), A, 77_000m, status: 0),
            Doc("d15e", 1, "002", "100", Oct(3), E, 410_000m),
            Doc("d15f", 1, "002", "100", Oct(3), F, 420_000m),
            Doc("d16g", 1, "002", "G-20", Oct(4), G, 999_000m),
            Doc("d16h", 1, "002", "H-20", Oct(4), H, 999_000m),
            Doc("d17a", 1, "002", "H-1", Oct(2), H, 555_000m),
            Doc("d17b", 1, "002", "H-2", Oct(3), H, 555_000m),
            Doc("d18", 1, "002", "G-30", Oct(5), G, 640_000m),
            Doc("d19", 1, "007", "Д-1", Oct(2), A, 1_500_000m),
            Doc("d20", 0, "005", "АКТ-5", Oct(20), B, 123_000m, seller: BAccount),
        ];
    }

    public static IReadOnlyList<GoldenMatch> ExpectedMatches { get; } =
    [
        new("1 Russian number", ReconciliationMatchType.Exact, ["p1"], ["d1"]),
        new("2 Uzbek Latin number", ReconciliationMatchType.Exact, ["p2"], ["d2"]),
        new("3 Uzbek Cyrillic number", ReconciliationMatchType.Exact, ["p3"], ["d3"]),
        new("4 unique amount without number", ReconciliationMatchType.Exact, ["p4"], ["d4"]),
        new("5 contract number", ReconciliationMatchType.Exact, ["p5"], ["d5"]),
        new("6 partial payments", ReconciliationMatchType.Partial, ["p6a", "p6b"], ["d6"]),
        new("7 one payment, several documents (amounts)", ReconciliationMatchType.OneToMany, ["p7"], ["d7a", "d7b"]),
        new("7b one payment, several documents (numbers)", ReconciliationMatchType.OneToMany, ["p7b"], ["d7d", "d7e"]),
        new("8 several payments, one document", ReconciliationMatchType.ManyToOne, ["p8a", "p8b"], ["d8"]),
        new("9 overpayment", ReconciliationMatchType.Partial, ["p9"], ["d9"]),
        new("10 double payment: the first one matches", ReconciliationMatchType.Exact, ["p10a"], ["d10"]),
        new("11 payment to another account (AccountMismatch)", ReconciliationMatchType.Exact, ["p11"], ["d11"]),
        new("15 same number at another counterparty", ReconciliationMatchType.Exact, ["p15"], ["d15e"]),
        new("16 same amount at another counterparty", ReconciliationMatchType.Exact, ["p16"], ["d16g"]),
    ];

    public static IReadOnlyList<GoldenNoMatch> ExpectedNotMatched { get; } =
    [
        new("10 double payment: the second one", ["p10b"], [], "possible duplicate of p10a"),
        new("12 without INN", ["p12"], [], "payments without an INN are never matched automatically"),
        new("13 payment in USD", ["p13"], ["d13"], "only UZS payments are matched"),
        new("14 draft document", ["p14"], ["d14"], "status 0 is not counted"),
        new("17 ambiguous candidates", ["p17"], ["d17a", "d17b"], "two documents of the same amount (expected: not found)"),
        new("18 opposite directions", ["p18"], ["d18"], "an outgoing payment never pays our outgoing document"),
        new("19 contract document type", ["p19"], ["d19"], "type 007 is not a paid type"),
        new("20 signed act without a payment", [], ["d20"], "nothing paid yet (G6)"),
    ];

    /// <summary>Configures the fake bank (UZS and USD accounts, only the golden operations) and the fake Didox of one tenant.</summary>
    public static void Apply(FakeBankScenario bank, FakeEdoScenario edo)
    {
        bank.Seed = 777;
        bank.AccountCount = 2;
        bank.AccountCurrencies = ["000", "840"];
        bank.OnlyFixedOperations = true;
        bank.FixedOperations.Clear();
        bank.FixedOperations.AddRange(Operations);
        edo.PageSize = 100;
        edo.UseDocuments(Documents());
    }

    /// <summary>Compares the stored non-rejected matches of the current tenant with the expected ones.</summary>
    public static Task<GoldenEvaluation> EvaluateAsync(ISynectDbContext db, FakeBankScenario bank, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var operationKeys = Operations.ToDictionary(o => bank.OperationIdOf(o.Key), o => o.Key);
            var paymentKeys = (await db.BankTransactions.AsNoTracking().Select(t => new { t.Id, t.OperationId }).ToListAsync(token))
                .Where(t => operationKeys.ContainsKey(t.OperationId)).ToDictionary(t => t.Id, t => operationKeys[t.OperationId]);
            var documentKeys = (await db.EdoDocuments.AsNoTracking().Select(d => new { d.Id, d.ExternalId }).ToListAsync(token))
                .ToDictionary(d => d.Id, d => d.ExternalId);
            var matches = await db.ReconciliationMatches.AsNoTracking().Where(m => m.Status != MatchStatus.Rejected).ToListAsync(token);
            var items = (await db.ReconciliationMatchItems.AsNoTracking().ToListAsync(token)).ToLookup(i => i.MatchId);

            static string Key(IEnumerable<string> keys) => string.Join(",", keys.Order(StringComparer.Ordinal));
            var produced = matches.Select(m => (Match: m,
                Payments: Key(items[m.Id].Where(i => i.Kind == MatchItemKind.BankTransaction).Select(i => paymentKeys.GetValueOrDefault(i.RefId, i.RefId.ToString()))),
                Documents: Key(items[m.Id].Where(i => i.Kind == MatchItemKind.EdoDocument).Select(i => documentKeys.GetValueOrDefault(i.RefId, i.RefId.ToString())))))
                .ToList();

            var errors = new List<string>();
            var correct = 0;
            foreach (var e in ExpectedMatches)
            {
                var hit = produced.FirstOrDefault(p => p.Payments == Key(e.Payments) && p.Documents == Key(e.Documents));
                if (hit.Match is null) { errors.Add($"MISSED     {e.Case}: expected {Key(e.Payments)} <-> {Key(e.Documents)}"); continue; }
                if (e.Type is { } type && hit.Match.MatchType != type)
                {
                    errors.Add($"WRONG TYPE {e.Case}: {hit.Match.MatchType} instead of {type} (confidence {hit.Match.Confidence})");
                    continue;
                }
                correct++;
            }
            foreach (var p in produced.Where(p => !ExpectedMatches.Any(e => Key(e.Payments) == p.Payments && Key(e.Documents) == p.Documents)))
            {
                var why = ExpectedNotMatched.FirstOrDefault(n => n.Payments.Any(k => p.Payments.Split(',').Contains(k)));
                errors.Add($"EXTRA      {p.Match.MatchType} {p.Payments} <-> {p.Documents} (confidence {p.Match.Confidence})" +
                           (why is null ? "" : $"; expected no match: {why.Case} ({why.Why})"));
            }
            var precision = produced.Count == 0 ? 1.0 : (double)correct / produced.Count;
            var recall = ExpectedMatches.Count == 0 ? 1.0 : (double)correct / ExpectedMatches.Count;
            var list = produced.Select(p => $"{p.Match.MatchType,-9} {p.Match.Confidence,3}  {p.Payments} <-> {p.Documents}").Order(StringComparer.Ordinal).ToList();
            return new GoldenEvaluation(ExpectedMatches.Count, produced.Count, correct, precision, recall, errors, list);
        }, ct);
}
