using System.Security.Cryptography;
using System.Text;
using Synect.Domain.Banking;
using Synect.Domain.Edo;
using Synect.Domain.Reconciliation;
using Synect.Domain.Text;

namespace Synect.Application.Reconciliation;

/// <summary>An error-free bank operation of one counterparty, as the engine sees it.</summary>
public sealed record PaymentInput(
    Guid Id, string Inn, TransactionDirection Direction, long Amount, string Currency, DateOnly Date, string? Purpose, string? CounterpartyAccount);

/// <summary>A Didox document of a paid type in a counted status. Amount is total_delivery_sum_with_vat (total_sum when that is 0).</summary>
public sealed record DocumentInput(
    Guid Id, string Inn, EdoDocumentOwner Owner, string DocType, string? Number, DateOnly? Date, string? ContractNumber,
    long Amount, string Currency, string? SellerAccount);

/// <summary>Amount of a payment or document already taken by a Confirmed match.</summary>
public sealed record AllocationLock(MatchItemKind Kind, Guid RefId, long Amount);

/// <summary>Why a match or finding exists: a code, the weight it added to the confidence and its parameters.</summary>
public sealed record ReconciliationReason(string Code, int Weight, IReadOnlyDictionary<string, object?>? Params = null);

public sealed record MatchAllocation(Guid RefId, long Amount);

/// <summary>A match the rules propose (before it is compared with the stored ones).</summary>
public sealed record ProposedMatch(
    string Inn, ReconciliationMatchType Type, EdoDocumentOwner Owner, string Currency, int Confidence, IReadOnlyList<ReconciliationReason> Reasons,
    IReadOnlyList<MatchAllocation> Payments, IReadOnlyList<MatchAllocation> Documents, long PaymentsTotal, long DocumentsTotal)
{
    /// <summary>Type, direction and allocations: equal signatures mean the same match (the stored row is kept).</summary>
    public string Signature
    {
        get
        {
            var sb = new StringBuilder().Append(Type).Append('|').Append(Owner).Append('|').Append(Currency);
            foreach (var p in Payments.OrderBy(x => x.RefId)) sb.Append("|p:").Append(p.RefId.ToString("N")).Append(':').Append(p.Amount);
            foreach (var d in Documents.OrderBy(x => x.RefId)) sb.Append("|d:").Append(d.RefId.ToString("N")).Append(':').Append(d.Amount);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..40].ToLowerInvariant();
        }
    }
}

public sealed record ProposedFinding(
    FindingKind Kind, string Inn, MatchItemKind SubjectKind, Guid SubjectId, MatchItemKind? RelatedKind, Guid? RelatedId,
    int Confidence, IReadOnlyList<ReconciliationReason> Reasons);

public sealed record EngineResult(IReadOnlyList<ProposedMatch> Matches, IReadOnlyList<ProposedFinding> Findings);

/// <summary>Reason codes stored in reconciliation_matches.reasons and reconciliation_findings.reasons.</summary>
public static class ReasonCodes
{
    public const string DocumentNumberInPurpose = "DocumentNumberInPurpose";
    public const string ContractNumberInPurpose = "ContractNumberInPurpose";
    public const string AmountEqual = "AmountEqual";
    public const string AmountSumEqual = "AmountSumEqual";
    public const string PartialPayment = "PartialPayment";
    public const string Overpaid = "Overpaid";
    public const string DateInWindow = "DateInWindow";
    public const string DateOutsideWindow = "DateOutsideWindow";
    public const string DocumentDateInPurpose = "DocumentDateInPurpose";
    public const string UniqueCandidate = "UniqueCandidate";
    public const string AmbiguousCandidates = "AmbiguousCandidates";
    public const string AccountMatch = "AccountMatch";
    public const string AccountMismatch = "AccountMismatch";
    public const string BelowThreshold = "BelowThreshold";
    public const string AlreadyMatched = "CandidateAlreadyMatched";
    public const string NoDocumentOrContractNumber = "NoDocumentOrContractNumber";
    public const string SameDocumentReference = "SameDocumentReference";
    public const string SamePurposeAndAmount = "SamePurposeAndAmount";
    public const string SameAmountNearDates = "SameAmountNearDates";
}

/// <summary>
/// Deterministic, explainable matching of one counterparty's payments with its documents (no machine learning).
/// Order: (1) document numbers found in the purpose; (2) equal amounts inside the date window; (3) exact combinations
/// (one payment for several documents, several payments for one document). Every candidate gets a confidence from the
/// signal weights; below the threshold nothing is created and the closest candidate becomes a hint. A payment or a
/// document takes part in at most one proposed match; amounts taken by Confirmed matches are subtracted first and pairs
/// of Rejected matches are never proposed again. Inputs are sorted, so the result does not depend on row order.
/// </summary>
public sealed class ReconciliationEngine(ReconciliationOptions options, PurposeParser parser)
{
    private const int MaxSubsetVisitsPerInn = 400_000;

    private sealed class Pay(PaymentInput input, ParsedPurpose parsed, long remaining)
    {
        public PaymentInput In { get; } = input;
        public ParsedPurpose Parsed { get; } = parsed;
        public long Remaining { get; } = remaining;
        public bool Used { get; set; }
        public Guid Id => In.Id;
        public DateOnly Date => In.Date;
    }

    private sealed class Doc(DocumentInput input, long remaining)
    {
        public DocumentInput In { get; } = input;
        public long Remaining { get; } = remaining;
        public bool Used { get; set; }
        public Guid Id => In.Id;
        public DateOnly Date => In.Date ?? DateOnly.MinValue;
        public bool HasDate => In.Date is not null;
    }

    private sealed class Candidate(List<Pay> payments, List<Doc> documents, bool unique, int ambiguous)
    {
        public List<Pay> Payments { get; } = payments;
        public List<Doc> Documents { get; } = documents;
        public bool Unique { get; } = unique;
        public int AmbiguousCount { get; } = ambiguous;
    }

    private sealed class Side
    {
        public required EdoDocumentOwner Owner { get; init; }
        public required List<Pay> Payments { get; init; }
        public required List<Doc> Documents { get; init; }
        public HashSet<(Guid, Guid)> Rejected { get; init; } = [];
        public List<ProposedMatch> Matches { get; } = [];
        public Dictionary<Guid, (int Score, Guid Other, List<ReconciliationReason> Reasons)> Hints { get; } = [];
        public int SubsetVisits { get; set; }
    }

    public ReconciliationOptions Options => options;

    /// <param name="inn">Counterparty INN; empty for payments without an INN (only NoReference findings are produced).</param>
    /// <param name="payments">Every error-free payment of the INN (any currency).</param>
    /// <param name="documents">Documents of paid types in counted statuses of the INN (any currency).</param>
    public EngineResult Run(
        string inn, IEnumerable<PaymentInput> payments, IEnumerable<DocumentInput> documents,
        IEnumerable<AllocationLock> locks, IEnumerable<(Guid PaymentId, Guid DocumentId)> rejectedPairs)
    {
        var allPayments = payments.OrderBy(p => p.Date).ThenBy(p => p.Id).ToList();
        var allDocuments = documents.OrderBy(d => d.Date ?? DateOnly.MinValue).ThenBy(d => d.Id).ToList();
        var parsed = allPayments.ToDictionary(p => p.Id, p => parser.Parse(p.Purpose));
        var findings = new List<ProposedFinding>();

        foreach (var p in allPayments.Where(p => !parsed[p.Id].HasReference))
            findings.Add(new ProposedFinding(FindingKind.NoReference, inn, MatchItemKind.BankTransaction, p.Id, null, null, 0,
                [new ReconciliationReason(ReasonCodes.NoDocumentOrContractNumber, 0)]));
        if (inn.Length == 0) return new EngineResult([], findings);

        findings.AddRange(Duplicates(inn, allPayments, parsed, allDocuments));

        var locked = locks.GroupBy(l => (l.Kind, l.RefId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var rejected = rejectedPairs.ToHashSet();
        long Remaining(MatchItemKind kind, Guid id, long amount) => amount - locked.GetValueOrDefault((kind, id));

        var docs = allDocuments.Where(d => d.Currency == options.Currency)
            .Select(d => new Doc(d, Remaining(MatchItemKind.EdoDocument, d.Id, d.Amount)))
            .Where(d => d.Remaining > 0).ToList();
        var pays = allPayments.Where(p => p.Currency == options.Currency)
            .Select(p => new Pay(p, parsed[p.Id], Remaining(MatchItemKind.BankTransaction, p.Id, p.Amount)))
            .Where(p => p.Remaining > 0).ToList();

        var matches = new List<ProposedMatch>();
        // We sell (outgoing document): money comes in. We buy (incoming document): money goes out.
        foreach (var (owner, direction) in new[] { (EdoDocumentOwner.Outgoing, TransactionDirection.Incoming), (EdoDocumentOwner.Incoming, TransactionDirection.Outgoing) })
        {
            var side = new Side
            {
                Owner = owner,
                Payments = pays.Where(p => p.In.Direction == direction).ToList(),
                Documents = docs.Where(d => d.In.Owner == owner).ToList(),
                Rejected = rejected,
            };
            if (side.Payments.Count == 0 && side.Documents.Count == 0) continue;
            ByNumber(side);
            ByAmount(side);
            OneToMany(side);
            ManyToOne(side);
            Hints(side, findings, inn);
            matches.AddRange(side.Matches);
        }
        return new EngineResult(matches, findings);
    }

    // ---- Phase 1: document numbers in the purpose -------------------------------------------------------------------

    private void ByNumber(Side side)
    {
        var links = new Dictionary<Pay, List<Doc>>();
        foreach (var p in side.Payments)
        {
            var linked = new List<Doc>();
            foreach (var reference in p.Parsed.Documents)
            {
                var hits = side.Documents.Where(d => !side.Rejected.Contains((p.Id, d.Id)) && ReferenceKey.Matches(reference, d.In.Number)).ToList();
                if (hits.Count == 0) continue;
                if (hits.Count > 1)
                {
                    // The same number on several documents of this counterparty: exact amount, then date window, then nearest date.
                    var exact = hits.Where(d => AmountEqual(p.Remaining, d.Remaining)).ToList();
                    if (exact.Count >= 1) hits = exact;
                    var inWindow = hits.Where(d => InWindow(p, d)).ToList();
                    if (inWindow.Count >= 1) hits = inWindow;
                    var nearest = hits.Select(d => (Doc: d, Gap: Math.Abs(p.Date.DayNumber - d.Date.DayNumber))).ToList();
                    var best = nearest.Min(x => x.Gap);
                    hits = nearest.Where(x => x.Gap == best).Select(x => x.Doc).ToList();
                    if (hits.Count > 1) continue; // still ambiguous: no link for this reference
                }
                if (!linked.Contains(hits[0])) linked.Add(hits[0]);
            }
            if (linked.Count > 0) links[p] = linked;
        }
        if (links.Count == 0) return;

        foreach (var (payments, documents) in Components(links))
        {
            foreach (var candidate in SplitByNumber(payments, documents, links))
                Accept(side, candidate, byNumber: true);
        }
    }

    /// <summary>Connected groups of payments and documents linked by numbers, in a stable order.</summary>
    private static List<(List<Pay> Payments, List<Doc> Documents)> Components(Dictionary<Pay, List<Doc>> links)
    {
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra.CompareTo(rb) < 0 ? rb : ra] = ra.CompareTo(rb) < 0 ? ra : rb;
        }
        foreach (var (p, ds) in links)
        {
            parent.TryAdd(p.Id, p.Id);
            foreach (var d in ds) { parent.TryAdd(d.Id, d.Id); Union(p.Id, d.Id); }
        }
        var docs = links.Values.SelectMany(x => x).Distinct().ToList();
        return links.Keys.GroupBy(p => Find(p.Id))
            .Select(g => (Payments: g.OrderBy(p => p.Date).ThenBy(p => p.Id).ToList(),
                          Documents: docs.Where(d => Find(d.Id) == g.Key).OrderBy(d => d.Date).ThenBy(d => d.Id).ToList()))
            .OrderBy(c => c.Payments[0].Date).ThenBy(c => c.Payments[0].Id)
            .ToList();
    }

    private IEnumerable<Candidate> SplitByNumber(List<Pay> payments, List<Doc> documents, Dictionary<Pay, List<Doc>> links)
    {
        if (payments.Count == 1) { yield return new Candidate(payments, documents, unique: true, ambiguous: 0); yield break; }
        if (documents.Count == 1) { yield return ManyForOne(payments, documents[0]); yield break; }

        // Several payments and several documents: each payment goes to its first linked document that still has room.
        var assigned = documents.ToDictionary(d => d, _ => new List<Pay>());
        foreach (var p in payments)
        {
            var target = links[p].OrderBy(d => d.Date).ThenBy(d => d.Id)
                .FirstOrDefault(d => assigned[d].Sum(x => x.Remaining) < d.Remaining) ?? links[p].OrderBy(d => d.Date).ThenBy(d => d.Id).First();
            assigned[target].Add(p);
        }
        foreach (var d in documents.Where(d => assigned[d].Count > 0))
            yield return assigned[d].Count == 1 ? new Candidate(assigned[d], [d], true, 0) : ManyForOne(assigned[d], d);
    }

    /// <summary>
    /// Several payments name one document: one payment of exactly the document amount wins (the earliest; the others may be
    /// double payments); else an exact subset; else all of them in date order until the document is covered.
    /// </summary>
    private Candidate ManyForOne(List<Pay> payments, Doc d)
    {
        var exact = payments.FirstOrDefault(p => AmountEqual(p.Remaining, d.Remaining));
        if (exact is not null) return new Candidate([exact], [d], true, 0);
        var visits = 0;
        var subset = Subsets(payments, p => p.Remaining, d.Remaining, 2, ref visits).FirstOrDefault();
        if (subset is not null) return new Candidate(subset, [d], true, 0);
        var taken = new List<Pay>();
        long sum = 0;
        foreach (var p in payments)
        {
            if (sum >= d.Remaining) break;
            taken.Add(p);
            sum += p.Remaining;
        }
        return new Candidate(taken, [d], true, 0);
    }

    // ---- Phase 2: equal amounts inside the date window --------------------------------------------------------------

    private void ByAmount(Side side)
    {
        var free = side.Payments.Where(p => !p.Used).ToList();
        var docs = side.Documents.Where(d => !d.Used && d.HasDate).ToList();
        if (free.Count == 0 || docs.Count == 0) return;

        var byPayment = new Dictionary<Pay, List<Doc>>();
        foreach (var p in free)
        {
            var c = docs.Where(d => AmountEqual(p.Remaining, d.Remaining) && InWindow(p, d) && !side.Rejected.Contains((p.Id, d.Id))).ToList();
            // A contract number in the purpose narrows equal-amount candidates to that contract.
            var byContract = c.Where(d => ContractMatches(p, d)).ToList();
            if (byContract.Count > 0) c = byContract;
            if (c.Count > 0) byPayment[p] = c;
        }
        var byDocument = new Dictionary<Doc, List<Pay>>();
        foreach (var (p, ds) in byPayment)
            foreach (var d in ds)
                (byDocument.TryGetValue(d, out var list) ? list : byDocument[d] = []).Add(p);

        var pairs = byPayment.SelectMany(kv => kv.Value.Select(d => (P: kv.Key, D: d)))
            .Select(x =>
            {
                var unique = byPayment[x.P].Count == 1 && byDocument[x.D].Count == 1;
                var ambiguous = Math.Max(byPayment[x.P].Count, byDocument[x.D].Count);
                var c = new Candidate([x.P], [x.D], unique, unique ? 0 : ambiguous);
                return (Candidate: c, Score: Evaluate(side, c, byNumber: false).Confidence);
            })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Payments[0].Date).ThenBy(x => x.Candidate.Payments[0].Id)
            .ThenBy(x => x.Candidate.Documents[0].Date).ThenBy(x => x.Candidate.Documents[0].Id)
            .ToList();
        foreach (var (candidate, _) in pairs)
        {
            if (candidate.Payments[0].Used || candidate.Documents[0].Used) continue;
            Accept(side, candidate, byNumber: false);
        }
    }

    // ---- Phase 3: exact combinations --------------------------------------------------------------------------------

    private void OneToMany(Side side)
    {
        foreach (var p in side.Payments.Where(p => !p.Used).ToList())
        {
            if (side.SubsetVisits > MaxSubsetVisitsPerInn) return;
            var near = side.Documents.Where(d => !d.Used && d.HasDate && InWindow(p, d) && !side.Rejected.Contains((p.Id, d.Id)))
                .OrderBy(d => Math.Abs(p.Date.DayNumber - d.Date.DayNumber)).ThenBy(d => d.Date).ThenBy(d => d.Id)
                .Take(options.MaxCombinationCandidates).OrderBy(d => d.Date).ThenBy(d => d.Id).ToList();
            if (near.Count < 2) continue;
            var visits = 0;
            var solutions = Subsets(near, d => d.Remaining, p.Remaining, 2, ref visits).Take(2).ToList();
            side.SubsetVisits += visits;
            if (solutions.Count == 0) continue;
            Accept(side, new Candidate([p], solutions[0], solutions.Count == 1, solutions.Count == 1 ? 0 : 2), byNumber: false);
        }
    }

    private void ManyToOne(Side side)
    {
        foreach (var d in side.Documents.Where(d => !d.Used && d.HasDate).ToList())
        {
            if (side.SubsetVisits > MaxSubsetVisitsPerInn) return;
            var near = side.Payments.Where(p => !p.Used && InWindow(p, d) && !side.Rejected.Contains((p.Id, d.Id)))
                .OrderBy(p => Math.Abs(p.Date.DayNumber - d.Date.DayNumber)).ThenBy(p => p.Date).ThenBy(p => p.Id)
                .Take(options.MaxCombinationCandidates).OrderBy(p => p.Date).ThenBy(p => p.Id).ToList();
            if (near.Count < 2) continue;
            var visits = 0;
            var solutions = Subsets(near, p => p.Remaining, d.Remaining, 2, ref visits).Take(2).ToList();
            side.SubsetVisits += visits;
            if (solutions.Count == 0) continue;
            Accept(side, new Candidate(solutions[0], [d], solutions.Count == 1, solutions.Count == 1 ? 0 : 2), byNumber: false);
        }
    }

    /// <summary>Subsets of <paramref name="items"/> (in their order) of size minSize..MaxCombinationSize whose amounts add up to target.</summary>
    private IEnumerable<List<T>> Subsets<T>(List<T> items, Func<T, long> amount, long target, int minSize, ref int visits)
    {
        var results = new List<List<T>>();
        var amounts = items.Select(amount).ToArray();
        var suffix = new long[amounts.Length + 1];
        for (var i = amounts.Length - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + amounts[i];
        var stack = new List<int>();
        var count = 0;
        var tolerance = options.AmountTolerance;
        var max = options.MaxCombinationSize;

        void Search(int start, long sum)
        {
            if (results.Count >= 2 || count > MaxSubsetVisitsPerInn) return;
            for (var i = start; i < amounts.Length; i++)
            {
                count++;
                var s = sum + amounts[i];
                if (s - target > tolerance) continue;               // amounts are positive: too much already
                if (s + suffix[i + 1] < target - tolerance) return; // even everything left is not enough
                stack.Add(i);
                if (stack.Count >= minSize && Math.Abs(s - target) <= tolerance) results.Add(stack.Select(k => items[k]).ToList());
                else if (stack.Count < max) Search(i + 1, s);
                stack.RemoveAt(stack.Count - 1);
                if (results.Count >= 2) return;
            }
        }

        Search(0, 0);
        visits += count;
        return results;
    }

    // ---- Evaluation -------------------------------------------------------------------------------------------------

    private void Accept(Side side, Candidate candidate, bool byNumber)
    {
        if (candidate.Payments.Any(p => p.Used) || candidate.Documents.Any(d => d.Used)) return;
        var match = Evaluate(side, candidate, byNumber);
        if (match.Confidence >= options.AutoMatchThreshold)
        {
            foreach (var p in candidate.Payments) p.Used = true;
            foreach (var d in candidate.Documents) d.Used = true;
            side.Matches.Add(match);
            return;
        }
        // Below the threshold: remember it as the closest candidate of every item.
        var reasons = match.Reasons.Append(new ReconciliationReason(ReasonCodes.BelowThreshold, 0,
            new Dictionary<string, object?> { ["threshold"] = options.AutoMatchThreshold })).ToList();
        foreach (var p in candidate.Payments) Hint(side, p.Id, match.Confidence, candidate.Documents[0].Id, reasons);
        foreach (var d in candidate.Documents) Hint(side, d.Id, match.Confidence, candidate.Payments[0].Id, reasons);
    }

    private static void Hint(Side side, Guid subject, int score, Guid other, List<ReconciliationReason> reasons)
    {
        if (side.Hints.TryGetValue(subject, out var existing) && existing.Score >= score) return;
        side.Hints[subject] = (score, other, reasons);
    }

    private ProposedMatch Evaluate(Side side, Candidate c, bool byNumber)
    {
        var w = options.Weights;
        var reasons = new List<ReconciliationReason>();
        var paymentsTotal = c.Payments.Sum(p => p.Remaining);
        var documentsTotal = c.Documents.Sum(d => d.Remaining);
        var single = c.Payments.Count == 1 && c.Documents.Count == 1;

        // Document numbers: every document named by at least one payment (or every payment naming its document).
        var named = c.Documents.Count(d => c.Payments.Any(p => p.Parsed.Documents.Any(r => ReferenceKey.Matches(r, d.In.Number))));
        var naming = c.Payments.Count(p => c.Documents.Any(d => p.Parsed.Documents.Any(r => ReferenceKey.Matches(r, d.In.Number))));
        var share = c.Documents.Count > 1 ? (double)named / c.Documents.Count : (double)naming / c.Payments.Count;
        var numberWeight = (int)Math.Round(w.DocumentNumber * share);
        if (numberWeight > 0)
            reasons.Add(new ReconciliationReason(ReasonCodes.DocumentNumberInPurpose, numberWeight, new Dictionary<string, object?>
            {
                ["numbers"] = c.Documents.Where(d => c.Payments.Any(p => p.Parsed.Documents.Any(r => ReferenceKey.Matches(r, d.In.Number))))
                    .Select(d => d.In.Number).ToList(),
                ["named_documents"] = named, ["documents"] = c.Documents.Count, ["naming_payments"] = naming, ["payments"] = c.Payments.Count,
            }));
        var hasNumber = numberWeight > 0;

        var contracts = c.Documents.Where(d => c.Payments.Any(p => ContractMatches(p, d))).Select(d => d.In.ContractNumber).Distinct().ToList();
        if (contracts.Count > 0)
            reasons.Add(new ReconciliationReason(ReasonCodes.ContractNumberInPurpose, w.ContractNumber,
                new Dictionary<string, object?> { ["contracts"] = contracts }));

        var difference = paymentsTotal - documentsTotal;
        var amounts = new Dictionary<string, object?> { ["payments_total"] = paymentsTotal, ["documents_total"] = documentsTotal };
        if (Math.Abs(difference) <= options.AmountTolerance)
            reasons.Add(new ReconciliationReason(single ? ReasonCodes.AmountEqual : ReasonCodes.AmountSumEqual, single ? w.Amount : w.AmountSum, amounts));
        else if (difference < 0)
            reasons.Add(new ReconciliationReason(ReasonCodes.PartialPayment, hasNumber ? w.PartialAmount : 0,
                new Dictionary<string, object?>(amounts) { ["remaining"] = -difference }));
        else
            reasons.Add(new ReconciliationReason(ReasonCodes.Overpaid, hasNumber ? w.Overpaid : 0,
                new Dictionary<string, object?>(amounts) { ["overpaid"] = difference }));

        var gaps = c.Payments.SelectMany(p => c.Documents.Where(d => d.HasDate).Select(d => p.Date.DayNumber - d.Date.DayNumber)).ToList();
        if (gaps.Count > 0 && gaps.All(g => g >= options.DateWindowFromDays && g <= options.DateWindowToDays))
            reasons.Add(new ReconciliationReason(ReasonCodes.DateInWindow, w.DateInWindow,
                new Dictionary<string, object?> { ["days_min"] = gaps.Min(), ["days_max"] = gaps.Max() }));
        else
            reasons.Add(new ReconciliationReason(ReasonCodes.DateOutsideWindow, 0, new Dictionary<string, object?>
            {
                ["days_min"] = gaps.Count > 0 ? gaps.Min() : null, ["days_max"] = gaps.Count > 0 ? gaps.Max() : null,
                ["window_from"] = options.DateWindowFromDays, ["window_to"] = options.DateWindowToDays,
            }));

        if (c.Documents.Any(d => d.In.Date is { } date && c.Payments.Any(p => p.Parsed.Dates.Contains(date))))
            reasons.Add(new ReconciliationReason(ReasonCodes.DocumentDateInPurpose, w.DocumentDateInPurpose));

        if (!byNumber)
        {
            if (c.Unique) reasons.Add(new ReconciliationReason(ReasonCodes.UniqueCandidate, w.UniqueCandidate));
            else reasons.Add(new ReconciliationReason(ReasonCodes.AmbiguousCandidates, 0,
                new Dictionary<string, object?> { ["candidates"] = c.AmbiguousCount }));
        }

        if (side.Owner == EdoDocumentOwner.Incoming)
        {
            var checkedPairs = c.Payments.SelectMany(p => c.Documents.Select(d => (p, d)))
                .Where(x => Digits(x.p.In.CounterpartyAccount).Length > 0 && Digits(x.d.In.SellerAccount).Length > 0).ToList();
            var mismatched = checkedPairs.Where(x => Digits(x.p.In.CounterpartyAccount) != Digits(x.d.In.SellerAccount)).ToList();
            if (mismatched.Count > 0)
                reasons.Add(new ReconciliationReason(ReasonCodes.AccountMismatch, w.AccountMismatch, new Dictionary<string, object?>
                {
                    ["payment_accounts"] = mismatched.Select(x => Mask(x.p.In.CounterpartyAccount)).Distinct().ToList(),
                    ["seller_accounts"] = mismatched.Select(x => Mask(x.d.In.SellerAccount)).Distinct().ToList(),
                }));
            else if (checkedPairs.Count > 0)
                reasons.Add(new ReconciliationReason(ReasonCodes.AccountMatch, w.AccountMatch));
        }

        var confidence = Math.Clamp(reasons.Sum(r => r.Weight), 0, 100);
        var type = Math.Abs(difference) > options.AmountTolerance ? ReconciliationMatchType.Partial
            : single ? ReconciliationMatchType.Exact
            : c.Payments.Count == 1 ? ReconciliationMatchType.OneToMany
            : ReconciliationMatchType.ManyToOne;

        // Allocation: the smaller side is allocated in full, the larger one oldest first up to that total.
        var allocatedTotal = Math.Min(paymentsTotal, documentsTotal);
        return new ProposedMatch(c.Documents[0].In.Inn, type, side.Owner, options.Currency, confidence, reasons,
            Allocate(c.Payments.Select(p => (p.Id, p.Remaining)), allocatedTotal),
            Allocate(c.Documents.Select(d => (d.Id, d.Remaining)), allocatedTotal),
            paymentsTotal, documentsTotal);
    }

    private static List<MatchAllocation> Allocate(IEnumerable<(Guid Id, long Amount)> items, long total)
    {
        var result = new List<MatchAllocation>();
        var left = total;
        foreach (var (id, amount) in items)
        {
            var take = Math.Min(amount, Math.Max(0, left));
            result.Add(new MatchAllocation(id, take));
            left -= take;
        }
        return result;
    }

    // ---- Hints and duplicates ---------------------------------------------------------------------------------------

    /// <summary>For every unmatched payment and document: the closest candidate on the other side, when it is meaningful.</summary>
    private void Hints(Side side, List<ProposedFinding> findings, string inn)
    {
        foreach (var p in side.Payments.Where(p => !p.Used))
        {
            var best = side.Hints.TryGetValue(p.Id, out var h) ? h : default;
            foreach (var d in side.Documents.Where(d => !side.Rejected.Contains((p.Id, d.Id)) && QuickScore(p, d) >= options.HintMinConfidence))
            {
                var m = Evaluate(side, new Candidate([p], [d], unique: false, ambiguous: 0), byNumber: true);
                var reasons = m.Reasons.ToList();
                if (d.Used) reasons.Add(new ReconciliationReason(ReasonCodes.AlreadyMatched, 0));
                var score = d.Used ? m.Confidence / 2 : m.Confidence;
                if (best.Reasons is null || score > best.Score) best = (score, d.Id, reasons);
            }
            if (best.Reasons is not null && best.Score >= options.HintMinConfidence)
                findings.Add(new ProposedFinding(FindingKind.NearestCandidate, inn, MatchItemKind.BankTransaction, p.Id,
                    MatchItemKind.EdoDocument, best.Other, best.Score, best.Reasons));
        }
        foreach (var d in side.Documents.Where(d => !d.Used))
        {
            var best = side.Hints.TryGetValue(d.Id, out var h) ? h : default;
            foreach (var p in side.Payments.Where(p => !side.Rejected.Contains((p.Id, d.Id)) && QuickScore(p, d) >= options.HintMinConfidence))
            {
                var m = Evaluate(side, new Candidate([p], [d], unique: false, ambiguous: 0), byNumber: true);
                var reasons = m.Reasons.ToList();
                if (p.Used) reasons.Add(new ReconciliationReason(ReasonCodes.AlreadyMatched, 0));
                var score = p.Used ? m.Confidence / 2 : m.Confidence;
                if (best.Reasons is null || score > best.Score) best = (score, p.Id, reasons);
            }
            if (best.Reasons is not null && best.Score >= options.HintMinConfidence)
                findings.Add(new ProposedFinding(FindingKind.NearestCandidate, inn, MatchItemKind.EdoDocument, d.Id,
                    MatchItemKind.BankTransaction, best.Other, best.Score, best.Reasons));
        }
    }

    /// <summary>
    /// Possible double payments: the same direction, currency and amount, and the same document named in both purposes
    /// (90 when that document is paid in full by either payment alone, 60 when the document is unknown; never when both
    /// together still fit into the document, which is an instalment), the same purpose within DuplicateSamePurposeDays (70),
    /// or dates within DuplicateSameAmountDays (50). The later payment is the subject, the earlier one is related.
    /// </summary>
    private IEnumerable<ProposedFinding> Duplicates(
        string inn, List<PaymentInput> payments, Dictionary<Guid, ParsedPurpose> parsed, List<DocumentInput> documents)
    {
        foreach (var group in payments.GroupBy(p => (p.Direction, p.Currency, p.Amount)).Where(g => g.Count() > 1))
        {
            var list = group.ToList(); // already sorted by date, id
            for (var i = 1; i < list.Count; i++)
            {
                var b = list[i];
                (int Score, PaymentInput Other, ReconciliationReason Reason)? best = null;
                for (var j = i - 1; j >= 0; j--)
                {
                    var a = list[j];
                    var gap = b.Date.DayNumber - a.Date.DayNumber;
                    if (gap > options.DateWindowToDays) break;
                    var shared = parsed[a.Id].Documents.SelectMany(r => r.Keys).Intersect(parsed[b.Id].Documents.SelectMany(r => r.Keys)).ToList();
                    var p = new Dictionary<string, object?> { ["days_between"] = gap, ["amount"] = b.Amount };
                    var sharedScore = 0;
                    var instalments = false;
                    if (shared.Count > 0)
                    {
                        var named = documents.Where(d => parsed[a.Id].Documents.Any(r => ReferenceKey.Matches(r, d.Number))
                                                         && parsed[b.Id].Documents.Any(r => ReferenceKey.Matches(r, d.Number))).ToList();
                        instalments = named.Count > 0 && named.All(d => d.Amount >= a.Amount + b.Amount);
                        sharedScore = named.Count == 0 ? 60 : named.Any(d => d.Amount <= b.Amount) ? 90 : 70;
                    }
                    if (instalments) continue; // both name one document and fit into it together: instalments, not a double payment
                    (int, PaymentInput, ReconciliationReason)? found =
                        sharedScore > 0 ? (sharedScore, a, new ReconciliationReason(ReasonCodes.SameDocumentReference, sharedScore, new Dictionary<string, object?>(p) { ["references"] = shared }))
                        : gap <= options.DuplicateSamePurposeDays && SamePurpose(a.Purpose, b.Purpose) ? (70, a, new ReconciliationReason(ReasonCodes.SamePurposeAndAmount, 70, p))
                        : gap <= options.DuplicateSameAmountDays ? (50, a, new ReconciliationReason(ReasonCodes.SameAmountNearDates, 50, p))
                        : null;
                    if (found is { } f && (best is null || f.Item1 > best.Value.Score)) best = f;
                }
                if (best is { } x)
                    yield return new ProposedFinding(FindingKind.PossibleDuplicate, inn, MatchItemKind.BankTransaction, b.Id,
                        MatchItemKind.BankTransaction, x.Other.Id, x.Score, [x.Reason]);
            }
        }
    }

    private static bool SamePurpose(string? a, string? b)
    {
        static string Norm(string? s) => string.Join(' ', LatinTransliterator.ToLatinLower(s).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var na = Norm(a);
        return na.Length > 0 && na == Norm(b);
    }

    /// <summary>Upper bound of a pair's confidence from the strong signals only, to skip hopeless pairs cheaply.</summary>
    private int QuickScore(Pay p, Doc d)
    {
        var w = options.Weights;
        var score = w.DateInWindow + w.DocumentDateInPurpose + Math.Max(0, w.AccountMatch);
        if (AmountEqual(p.Remaining, d.Remaining)) score += w.Amount;
        else score += Math.Max(w.PartialAmount, w.Overpaid);
        if (p.Parsed.Documents.Any(r => ReferenceKey.Matches(r, d.In.Number))) score += w.DocumentNumber;
        else if (!AmountEqual(p.Remaining, d.Remaining)) return 0; // no number and different amounts: never a useful hint
        if (ContractMatches(p, d)) score += w.ContractNumber;
        return score;
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private bool AmountEqual(long a, long b) => Math.Abs(a - b) <= options.AmountTolerance;

    private bool InWindow(Pay p, Doc d)
    {
        if (!d.HasDate) return false;
        var gap = p.Date.DayNumber - d.Date.DayNumber;
        return gap >= options.DateWindowFromDays && gap <= options.DateWindowToDays;
    }

    private static bool ContractMatches(Pay p, Doc d) =>
        !string.IsNullOrWhiteSpace(d.In.ContractNumber) && p.Parsed.Contracts.Any(r => ReferenceKey.Matches(r, d.In.ContractNumber));

    private static string Digits(string? account) => account is null ? string.Empty : new string(account.Where(char.IsAsciiDigit).ToArray());

    private static string? Mask(string? account) =>
        account is null ? null : account.Trim().Length <= 4 ? "****" : "****" + account.Trim()[^4..];
}
