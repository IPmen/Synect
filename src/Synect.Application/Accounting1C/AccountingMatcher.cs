using Synect.Application.Reconciliation;

namespace Synect.Application.Accounting1C;

/// <summary>Accounting:* settings of the comparison of 1C data with the bank and Didox. Assumptions to check on real data.</summary>
public sealed class AccountingOptions
{
    /// <summary>A bank payment and a 1C bank document are the same payment when their dates differ by at most this many days.</summary>
    public int PaymentDateWindowDays { get; set; } = 3;

    /// <summary>A Didox document and a 1C document without a common number are the same when their dates differ by at most this.</summary>
    public int DocumentDateWindowDays { get; set; } = 5;

    /// <summary>Most rows of one side a comparison reads; a longer period must be narrowed.</summary>
    public int MaxCompareRows { get; set; } = 50_000;
}

/// <summary>Kind of a difference between 1C and another source.</summary>
public enum AccountingDifferenceKind
{
    /// <summary>In the bank statement (or Didox), not found in 1C.</summary>
    MissingIn1C = 1,

    /// <summary>In 1C, not found in the bank statement (or Didox).</summary>
    MissingInSource = 2,

    /// <summary>Found on both sides with different amounts.</summary>
    AmountDiffers = 3,

    /// <summary>Found in 1C, but the 1C document is not posted.</summary>
    NotPostedIn1C = 4,
}

/// <summary>One side of a comparison: a bank payment, a Didox document or a 1C document.</summary>
/// <param name="Direction">Incoming / outgoing money, or outgoing (we sell) / incoming (we buy) documents, as one flag: true = incoming money or a sale.</param>
/// <param name="Keys">Number keys (see <see cref="ReferenceKey"/>) and exact identifiers the item can be matched by.</param>
public sealed record MatchItem(string Id, DateOnly Date, long Amount, string Currency, bool Direction, string? Inn, bool Posted,
    IReadOnlyList<string> Keys, string? ExactId = null);

/// <summary>A pair found on both sides, or an item found on one side only.</summary>
public sealed record MatchResult(MatchItem? Source, MatchItem? Accounting, AccountingDifferenceKind? Difference, string? MatchedBy);

/// <summary>
/// Deterministic pairing of 1C records with bank payments and Didox documents (no machine learning; every pair has its
/// reason). Each item is used in at most one pair; ties are broken by date and id, so the result does not depend on the
/// order of the input.
/// </summary>
public static class AccountingMatcher
{
    /// <summary>
    /// Bank payments against 1C bank documents: (1) same direction, currency, amount and INN, dates within the window;
    /// (2) the same when one side has no INN; (3) same direction, currency and INN with another amount (amount differs).
    /// </summary>
    public static IReadOnlyList<MatchResult> MatchPayments(IReadOnlyList<MatchItem> bank, IReadOnlyList<MatchItem> accounting, int windowDays)
    {
        var state = new State(bank, accounting);
        state.Pair(x => (x.Direction, x.Currency, x.Amount, Inn: x.Inn ?? ""), i => !string.IsNullOrEmpty(i.Inn), windowDays, "amount, INN and date");
        state.Pair(x => (x.Direction, x.Currency, x.Amount), _ => true, windowDays, "amount and date (one side has no INN)", requireMissingInn: true);
        state.Pair(x => (x.Direction, x.Currency, Inn: x.Inn ?? ""), i => !string.IsNullOrEmpty(i.Inn), windowDays, "INN and date", amountDiffers: true);
        return state.Results();
    }

    /// <summary>
    /// Didox documents against 1C documents: (1) the Didox id stored in the 1C document (esf_id); (2) same direction, INN
    /// and document number; (3) same direction, INN and amount with dates within the window. A pair with different amounts
    /// is "amount differs".
    /// </summary>
    public static IReadOnlyList<MatchResult> MatchDocuments(IReadOnlyList<MatchItem> didox, IReadOnlyList<MatchItem> accounting, int windowDays)
    {
        var state = new State(didox, accounting);
        state.PairByKeys(i => i.ExactId is null ? [] : [i.ExactId], "Didox id (esf_id)");
        state.PairByKeys(i => i.Keys.Select(k => $"{i.Direction}|{i.Inn}|{k}"), "document number and INN", requireInn: true);
        state.Pair(x => (x.Direction, x.Currency, x.Amount, Inn: x.Inn ?? ""), i => !string.IsNullOrEmpty(i.Inn), windowDays, "amount, INN and date");
        return state.Results();
    }

    private sealed class State(IReadOnlyList<MatchItem> source, IReadOnlyList<MatchItem> accounting)
    {
        private readonly List<MatchItem> _source = source.OrderBy(i => i.Date).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        private readonly List<MatchItem> _accounting = accounting.OrderBy(i => i.Date).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        private readonly List<MatchResult> _pairs = [];
        private readonly HashSet<string> _usedSource = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedAccounting = new(StringComparer.Ordinal);

        /// <summary>Within each group, every source item (oldest first) takes the nearest free 1C item inside the window.</summary>
        public void Pair<TKey>(Func<MatchItem, TKey> key, Func<MatchItem, bool> eligible, int windowDays, string reason,
            bool amountDiffers = false, bool requireMissingInn = false) where TKey : notnull
        {
            var candidates = _accounting.Where(a => !_usedAccounting.Contains(a.Id) && (requireMissingInn || eligible(a)))
                .GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var s in _source)
            {
                if (_usedSource.Contains(s.Id) || (!requireMissingInn && !eligible(s))) continue;
                if (!candidates.TryGetValue(key(s), out var list)) continue;
                MatchItem? best = null;
                foreach (var a in list)
                {
                    if (_usedAccounting.Contains(a.Id)) continue;
                    // The pass without INN is only for pairs where at least one side has none.
                    if (requireMissingInn && !string.IsNullOrEmpty(s.Inn) && !string.IsNullOrEmpty(a.Inn)) continue;
                    if (amountDiffers && a.Amount == s.Amount) continue;
                    var distance = Math.Abs(a.Date.DayNumber - s.Date.DayNumber);
                    if (distance > windowDays) continue;
                    if (best is null || distance < Math.Abs(best.Date.DayNumber - s.Date.DayNumber)) best = a;
                }
                if (best is not null) Add(s, best, reason);
            }
        }

        /// <summary>Pairs items that share a key; an ambiguous key (several free items on a side) pairs the nearest dates.</summary>
        public void PairByKeys(Func<MatchItem, IEnumerable<string>> keys, string reason, bool requireInn = false)
        {
            var index = new Dictionary<string, List<MatchItem>>(StringComparer.Ordinal);
            foreach (var a in _accounting)
            {
                if (_usedAccounting.Contains(a.Id) || (requireInn && string.IsNullOrEmpty(a.Inn))) continue;
                foreach (var k in keys(a).Distinct(StringComparer.Ordinal))
                {
                    if (!index.TryGetValue(k, out var list)) index[k] = list = [];
                    list.Add(a);
                }
            }
            foreach (var s in _source)
            {
                if (_usedSource.Contains(s.Id) || (requireInn && string.IsNullOrEmpty(s.Inn))) continue;
                var best = keys(s).Distinct(StringComparer.Ordinal)
                    .SelectMany(k => index.TryGetValue(k, out var list) ? list : [])
                    .Where(a => !_usedAccounting.Contains(a.Id))
                    .OrderBy(a => Math.Abs(a.Date.DayNumber - s.Date.DayNumber)).ThenBy(a => a.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best is not null) Add(s, best, reason);
            }
        }

        private void Add(MatchItem s, MatchItem a, string reason)
        {
            _usedSource.Add(s.Id);
            _usedAccounting.Add(a.Id);
            AccountingDifferenceKind? difference = s.Amount != a.Amount || s.Currency != a.Currency
                ? AccountingDifferenceKind.AmountDiffers
                : a.Posted ? null : AccountingDifferenceKind.NotPostedIn1C;
            _pairs.Add(new MatchResult(s, a, difference, reason));
        }

        public IReadOnlyList<MatchResult> Results() =>
            _pairs
                .Concat(_source.Where(s => !_usedSource.Contains(s.Id)).Select(s => new MatchResult(s, null, AccountingDifferenceKind.MissingIn1C, null)))
                .Concat(_accounting.Where(a => !_usedAccounting.Contains(a.Id)).Select(a => new MatchResult(null, a, AccountingDifferenceKind.MissingInSource, null)))
                .ToList();
    }
}
