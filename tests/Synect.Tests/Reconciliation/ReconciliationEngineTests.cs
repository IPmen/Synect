using System.Security.Cryptography;
using System.Text;
using Synect.Application.Reconciliation;
using Synect.Domain.Banking;
using Synect.Domain.Edo;
using Synect.Domain.Reconciliation;

namespace Synect.Tests.Reconciliation;

/// <summary>The matching rules in memory (no database): types, priorities, thresholds, determinism, decisions.</summary>
public class ReconciliationEngineTests
{
    private const string Inn = "301000001";

    private static Guid Id(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    private static PaymentInput Pay(string key, int day, long amount, string purpose = "", TransactionDirection dir = TransactionDirection.Incoming,
        string currency = "UZS", string? account = null) =>
        new(Id(key), Inn, dir, amount, currency, new DateOnly(2026, 10, day), purpose, account);

    private static DocumentInput Doc(string key, int day, long amount, string? number = null, EdoDocumentOwner owner = EdoDocumentOwner.Outgoing,
        string? contract = null, string? seller = null, string currency = "UZS") =>
        new(Id(key), Inn, owner, "002", number, new DateOnly(2026, 10, day), contract, amount, currency, seller);

    private static ReconciliationEngine Engine(Action<ReconciliationOptions>? configure = null)
    {
        var options = new ReconciliationOptions();
        configure?.Invoke(options);
        return new ReconciliationEngine(options, PurposeParser.Default);
    }

    private static EngineResult Run(IEnumerable<PaymentInput> payments, IEnumerable<DocumentInput> documents, ReconciliationEngine? engine = null,
        IEnumerable<AllocationLock>? locks = null, IEnumerable<(Guid, Guid)>? rejected = null) =>
        (engine ?? Engine()).Run(Inn, payments, documents, locks ?? [], rejected ?? []);

    private static (string[] Payments, string[] Documents) Keys(ProposedMatch m, params string[] keys)
    {
        var byId = keys.ToDictionary(Id, k => k);
        return (m.Payments.Select(p => byId[p.RefId]).Order().ToArray(), m.Documents.Select(d => byId[d.RefId]).Order().ToArray());
    }

    private static IEnumerable<string> Codes(ProposedMatch m) => m.Reasons.Select(r => r.Code);

    [Fact]
    public void Exact_match_by_document_number_and_amount()
    {
        var r = Run([Pay("p", 5, 100_00, "Оплата по счет-фактуре №15 от 01.10.2026")], [Doc("d", 1, 100_00, "15")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(ReconciliationMatchType.Exact, m.Type);
        Assert.Equal(100, m.Confidence); // number 50 + amount 35 + window 10 + document date 5
        Assert.Equal([ReasonCodes.DocumentNumberInPurpose, ReasonCodes.AmountEqual, ReasonCodes.DateInWindow, ReasonCodes.DocumentDateInPurpose], Codes(m));
        Assert.Equal([new MatchAllocation(Id("p"), 100_00)], m.Payments);
        Assert.Equal([new MatchAllocation(Id("d"), 100_00)], m.Documents);
    }

    [Fact]
    public void Document_number_wins_over_an_equal_amount()
    {
        // The amount equals d1, but the purpose names d2.
        var r = Run([Pay("p", 10, 100_00, "Оплата по с/ф №2")], [Doc("d1", 1, 100_00, "1"), Doc("d2", 2, 150_00, "2")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(["d2"], Keys(m, "p", "d1", "d2").Documents);
        Assert.Equal(ReconciliationMatchType.Partial, m.Type);
        Assert.Contains(ReasonCodes.PartialPayment, Codes(m));
    }

    [Fact]
    public void Unique_amount_inside_the_window_matches_without_a_number()
    {
        var r = Run([Pay("p", 20, 333_33, "Оплата за услуги")], [Doc("d", 6, 333_33, "46"), Doc("other", 6, 100_00, "47")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(70, m.Confidence); // amount 35 + window 10 + unique 25 = the default threshold
        Assert.Contains(ReasonCodes.UniqueCandidate, Codes(m));
    }

    [Fact]
    public void Equal_amount_outside_the_date_window_does_not_match()
    {
        var r = Run([Pay("p", 20, 333_33, "Оплата")], [new DocumentInput(Id("d"), Inn, EdoDocumentOwner.Outgoing, "002", "46",
            new DateOnly(2025, 1, 1), null, 333_33, "UZS", null)]);
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Ambiguous_candidates_are_not_matched_but_hinted()
    {
        var r = Run([Pay("p", 10, 555_00, "Оплата")], [Doc("d1", 2, 555_00, "H-1"), Doc("d2", 3, 555_00, "H-2")]);
        Assert.Empty(r.Matches);
        var hint = Assert.Single(r.Findings, f => f.Kind == FindingKind.NearestCandidate && f.SubjectId == Id("p"));
        Assert.Equal(45, hint.Confidence);
        Assert.Contains(hint.RelatedId!.Value, new[] { Id("d1"), Id("d2") });
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.NearestCandidate && f.SubjectId == Id("d1"));
    }

    [Fact]
    public void One_payment_for_several_documents_by_a_unique_exact_subset()
    {
        var r = Run([Pay("p", 12, 1_000_00, "Оплата за товар")],
            [Doc("a", 1, 400_00, "F-1"), Doc("b", 5, 600_00, "F-2"), Doc("c", 9, 250_00, "F-3")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(ReconciliationMatchType.OneToMany, m.Type);
        Assert.Equal(["a", "b"], Keys(m, "p", "a", "b", "c").Documents);
        Assert.Contains(ReasonCodes.AmountSumEqual, Codes(m));
    }

    [Fact]
    public void Two_exact_subsets_are_ambiguous()
    {
        var r = Run([Pay("p", 12, 1_000_00, "Оплата")],
            [Doc("a", 1, 400_00), Doc("b", 5, 600_00), Doc("c", 6, 300_00), Doc("e", 7, 700_00)]);
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Several_payments_for_one_document_by_number()
    {
        var r = Run([Pay("p1", 12, 450_00, "Оплата по акту №Б-80, часть 1"), Pay("p2", 19, 450_00, "Оплата по акту №Б-80, часть 2")],
            [Doc("d", 10, 900_00, "Б-80")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(ReconciliationMatchType.ManyToOne, m.Type);
        Assert.Equal(["p1", "p2"], Keys(m, "p1", "p2", "d").Payments);
    }

    [Fact]
    public void Partial_payments_and_overpayment()
    {
        var partial = Assert.Single(Run([Pay("p", 10, 30_00, "Оплата по с/ф 5")], [Doc("d", 1, 100_00, "5")]).Matches);
        Assert.Equal(ReconciliationMatchType.Partial, partial.Type);
        Assert.Equal(75, partial.Confidence);
        Assert.Equal(70_00L, partial.Reasons.Single(x => x.Code == ReasonCodes.PartialPayment).Params!["remaining"]);
        Assert.Equal(30_00, partial.Documents.Single().Amount); // the document is allocated up to the payment

        var over = Assert.Single(Run([Pay("p", 10, 120_00, "Оплата по с/ф 5")], [Doc("d", 1, 100_00, "5")]).Matches);
        Assert.Equal(ReconciliationMatchType.Partial, over.Type);
        Assert.Contains(ReasonCodes.Overpaid, Codes(over));
        Assert.Equal(100_00, over.Payments.Single().Amount); // the payment is allocated up to the document
    }

    [Fact]
    public void Partial_amount_without_a_number_is_never_matched()
    {
        Assert.Empty(Run([Pay("p", 10, 30_00, "Оплата")], [Doc("d", 1, 100_00, "5")]).Matches);
    }

    [Fact]
    public void Opposite_directions_never_match()
    {
        // Our outgoing document (we sell) and an outgoing payment (we pay): never a pair, even with the number.
        var r = Run([Pay("p", 8, 640_00, "Возврат по с/ф G-30", TransactionDirection.Outgoing)], [Doc("d", 5, 640_00, "G-30")]);
        Assert.Empty(r.Matches);
        var incoming = Run([Pay("p", 8, 640_00, "Оплата по с/ф G-30", TransactionDirection.Outgoing)], [Doc("d", 5, 640_00, "G-30", EdoDocumentOwner.Incoming)]);
        Assert.Single(incoming.Matches);
    }

    [Fact]
    public void Other_currencies_are_excluded()
    {
        Assert.Empty(Run([Pay("p", 8, 50_00, "Payment for invoice SF-105", currency: "USD")], [Doc("d", 5, 50_00, "SF-105")]).Matches);
    }

    [Fact]
    public void Account_mismatch_is_a_warning_not_a_veto()
    {
        var r = Run([Pay("p", 7, 800_00, "Оплата по счету-фактуре D-9", TransactionDirection.Outgoing, account: "20208000900000000099")],
            [Doc("d", 5, 800_00, "D-9", EdoDocumentOwner.Incoming, seller: "20208000900000000001")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(80, m.Confidence);
        Assert.Contains(ReasonCodes.AccountMismatch, Codes(m));
        var same = Assert.Single(Run([Pay("p", 7, 800_00, "Оплата по с/ф D-9", TransactionDirection.Outgoing, account: "20208000900000000001")],
            [Doc("d", 5, 800_00, "D-9", EdoDocumentOwner.Incoming, seller: "20208000900000000001")]).Matches);
        Assert.Contains(ReasonCodes.AccountMatch, Codes(same));
    }

    [Fact]
    public void Contract_number_narrows_equal_amounts()
    {
        var r = Run([Pay("p", 8, 200_00, "Оплата по договору №15/2026", TransactionDirection.Outgoing)],
            [Doc("a", 4, 200_00, "Б-77", EdoDocumentOwner.Incoming, contract: "15/2026"), Doc("b", 5, 200_00, "Б-78", EdoDocumentOwner.Incoming, contract: "16/2026")]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(["a"], Keys(m, "p", "a", "b").Documents);
        Assert.Contains(ReasonCodes.ContractNumberInPurpose, Codes(m));
    }

    [Fact]
    public void Threshold_decides_whether_a_match_is_created()
    {
        // Number + partial amount + date window = 75.
        PaymentInput[] p = [Pay("p", 5, 30_00, "Оплата по с/ф №15")];
        DocumentInput[] d = [Doc("d", 1, 100_00, "15")];
        Assert.Equal(75, Assert.Single(Run(p, d).Matches).Confidence);
        var strict = Run(p, d, Engine(o => o.AutoMatchThreshold = 76));
        Assert.Empty(strict.Matches);
        var hint = Assert.Single(strict.Findings, f => f.Kind == FindingKind.NearestCandidate && f.SubjectId == Id("p"));
        Assert.Equal(75, hint.Confidence);
        Assert.Contains(hint.Reasons, x => x.Code == ReasonCodes.BelowThreshold);
        // A lower threshold accepts amount + date window (45) for a unique pair even without the uniqueness bonus.
        Assert.Single(Run([Pay("p", 10, 555_00, "Оплата")], [Doc("d1", 2, 555_00), Doc("d2", 3, 555_00)], Engine(o => o.AutoMatchThreshold = 40)).Matches);
    }

    [Fact]
    public void Weights_and_tolerance_come_from_the_settings()
    {
        PaymentInput[] p = [Pay("p", 5, 100_05, "Оплата по с/ф №15")];
        DocumentInput[] d = [Doc("d", 1, 100_00, "15")];
        Assert.Equal(ReconciliationMatchType.Partial, Assert.Single(Run(p, d).Matches).Type);
        Assert.Equal(ReconciliationMatchType.Exact, Assert.Single(Run(p, d, Engine(o => o.AmountTolerance = 10)).Matches).Type);
        Assert.Equal(55, Assert.Single(Run([Pay("p", 5, 100_00, "Оплата по с/ф №15")], d, Engine(o => { o.Weights.DocumentNumber = 10; o.AutoMatchThreshold = 50; })).Matches).Confidence);
        Assert.NotEqual(new ReconciliationOptions().RulesVersion, new ReconciliationOptions { AutoMatchThreshold = 71 }.RulesVersion);
    }

    [Fact]
    public void Double_payment_matches_the_first_and_flags_the_second()
    {
        var r = Run([Pay("a", 11, 250_00, "Оплата по счет-фактуре СФ-104"), Pay("b", 12, 250_00, "Оплата по счет-фактуре СФ-104")],
            [Doc("d", 9, 250_00, "СФ-104")]);
        Assert.Equal(["a"], Keys(Assert.Single(r.Matches), "a", "b", "d").Payments);
        var dup = Assert.Single(r.Findings, f => f.Kind == FindingKind.PossibleDuplicate);
        Assert.Equal((Id("b"), Id("a"), 90), (dup.SubjectId, dup.RelatedId!.Value, dup.Confidence));
    }

    [Fact]
    public void Instalments_of_one_document_are_not_duplicates()
    {
        var r = Run([Pay("a", 10, 100_00, "Оплата по с/ф №7"), Pay("b", 20, 100_00, "Оплата по с/ф №7")], [Doc("d", 1, 300_00, "7")]);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.PossibleDuplicate);
        Assert.Equal(ReconciliationMatchType.Partial, Assert.Single(r.Matches).Type);
    }

    [Fact]
    public void Rejected_pairs_are_never_proposed_again_and_confirmed_amounts_are_subtracted()
    {
        PaymentInput[] p = [Pay("p", 5, 100_00, "Оплата по с/ф №15")];
        DocumentInput[] d = [Doc("d", 1, 100_00, "15")];
        Assert.Empty(Run(p, d, rejected: [(Id("p"), Id("d"))]).Matches);

        // 60 of the document is already in a confirmed match; a new payment of 40 naming it closes the rest exactly.
        var r = Run([Pay("q", 9, 40_00, "Доплата по с/ф №15")], d, locks: [new AllocationLock(MatchItemKind.EdoDocument, Id("d"), 60_00)]);
        var m = Assert.Single(r.Matches);
        Assert.Equal(ReconciliationMatchType.Exact, m.Type);
        Assert.Equal(40_00, m.Documents.Single().Amount);
        // A fully confirmed document takes no part any more.
        Assert.Empty(Run([Pay("q", 9, 40_00, "Доплата по с/ф №15")], d, locks: [new AllocationLock(MatchItemKind.EdoDocument, Id("d"), 100_00)]).Matches);
    }

    [Fact]
    public void Without_inn_only_missing_references_are_reported()
    {
        var r = Engine().Run("", [Pay("p", 5, 100_00, "Взнос наличных") with { Inn = "" }], [], [], []);
        Assert.Empty(r.Matches);
        Assert.Equal(FindingKind.NoReference, Assert.Single(r.Findings).Kind);
    }

    [Fact]
    public void No_payment_or_document_is_used_twice_and_row_order_does_not_matter()
    {
        var random = new Random(12345);
        var payments = new List<PaymentInput>();
        var documents = new List<DocumentInput>();
        for (var i = 0; i < 60; i++)
        {
            var amount = random.Next(1, 8) * 100_00L;
            documents.Add(Doc("d" + i, random.Next(1, 20), amount, (i % 3 == 0 ? "СФ-" : "") + i,
                i % 4 == 0 ? EdoDocumentOwner.Incoming : EdoDocumentOwner.Outgoing));
            var purpose = random.Next(4) switch { 0 => $"Оплата по с/ф №{(random.Next(3) == 0 ? "СФ-" : "")}{random.Next(60)}", 1 => "Оплата", 2 => $"Оплата по с/ф {i} и {i + 1}", _ => "" };
            payments.Add(Pay("p" + i, random.Next(1, 28), random.Next(1, 8) * 100_00L, purpose,
                random.Next(4) == 0 ? TransactionDirection.Outgoing : TransactionDirection.Incoming));
        }

        var r = Run(payments, documents);
        Assert.NotEmpty(r.Matches);
        var usedPayments = r.Matches.SelectMany(m => m.Payments.Select(p => p.RefId)).ToList();
        var usedDocuments = r.Matches.SelectMany(m => m.Documents.Select(d => d.RefId)).ToList();
        Assert.Equal(usedPayments.Count, usedPayments.Distinct().Count());
        Assert.Equal(usedDocuments.Count, usedDocuments.Distinct().Count());
        Assert.All(r.Matches, m => Assert.True(m.Confidence >= 70));
        Assert.All(r.Matches, m => Assert.Equal(m.Payments.Sum(p => p.Amount), m.Documents.Sum(d => d.Amount)));

        string Fingerprint(EngineResult x) => string.Join("\n",
            x.Matches.Select(m => $"{m.Signature}:{m.Confidence}").Order(StringComparer.Ordinal)
                .Concat(x.Findings.Select(f => $"{f.Kind}:{f.SubjectId}:{f.RelatedId}:{f.Confidence}").Order(StringComparer.Ordinal)));
        var expected = Fingerprint(r);
        for (var seed = 1; seed <= 5; seed++)
        {
            var shuffle = new Random(seed);
            var shuffled = Run(payments.OrderBy(_ => shuffle.Next()).ToList(), documents.OrderBy(_ => shuffle.Next()).ToList());
            Assert.Equal(expected, Fingerprint(shuffled));
        }
    }
}
