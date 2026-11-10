using System.Security.Cryptography;
using System.Text;

namespace Synect.Application.Reconciliation;

/// <summary>
/// Reconciliation section. Every value is an ASSUMPTION to be checked on real data (see the README); changing any of them
/// changes <see cref="RulesVersion"/>.
/// </summary>
public sealed class ReconciliationOptions
{
    /// <summary>Code version of the rules; the rules version stored with every run is this plus a hash of the settings.</summary>
    public const string CodeVersion = "r1";

    /// <summary>Only payments in this currency are matched (Didox documents are in UZS); others are listed as excluded.</summary>
    public string Currency { get; set; } = "UZS";

    /// <summary>Didox document types that are paid for: invoices 002, 008, 023 and acts 005. Contracts, powers of attorney,
    /// reconciliation acts and the like are never matched.</summary>
    public string[] DocumentTypes { get; set; } = ["002", "008", "023", "005"];

    /// <summary>Document types reported as acts (G6).</summary>
    public string[] ActDocumentTypes { get; set; } = ["005"];

    /// <summary>Didox status "signed" (G6).</summary>
    public string SignedStatus { get; set; } = "3";

    /// <summary>Largest difference in minor units for amounts to count as equal.</summary>
    public long AmountTolerance { get; set; }

    /// <summary>Payment date minus document date must be within [DateWindowFromDays, DateWindowToDays].</summary>
    public int DateWindowFromDays { get; set; } = -30;

    public int DateWindowToDays { get; set; } = 180;

    /// <summary>Matches below this confidence are not created; the items stay unmatched with a hint.</summary>
    public int AutoMatchThreshold { get; set; } = 70;

    /// <summary>The closest candidate is stored as a hint only from this confidence.</summary>
    public int HintMinConfidence { get; set; } = 30;

    /// <summary>Combination search (OneToMany, ManyToOne): at most this many nearest candidates...</summary>
    public int MaxCombinationCandidates { get; set; } = 12;

    /// <summary>...and subsets of at most this many items.</summary>
    public int MaxCombinationSize { get; set; } = 6;

    /// <summary>Same amount and the same purpose within this many days: possible double payment.</summary>
    public int DuplicateSamePurposeDays { get; set; } = 30;

    /// <summary>Same amount within this many days (purpose differs): possible double payment, lower confidence.</summary>
    public int DuplicateSameAmountDays { get; set; } = 3;

    /// <summary>Recalculate the touched INNs after every successful load.</summary>
    public bool RunAfterLoad { get; set; } = true;

    public ReconciliationWeights Weights { get; set; } = new();

    public string RulesVersion
    {
        get
        {
            var w = Weights;
            var text = string.Join("|", Currency, string.Join(",", DocumentTypes.Distinct().Order(StringComparer.Ordinal)), AmountTolerance, DateWindowFromDays, DateWindowToDays,
                AutoMatchThreshold, HintMinConfidence, MaxCombinationCandidates, MaxCombinationSize, DuplicateSamePurposeDays,
                DuplicateSameAmountDays, w.DocumentNumber, w.ContractNumber, w.Amount, w.AmountSum, w.PartialAmount, w.Overpaid,
                w.DateInWindow, w.DocumentDateInPurpose, w.UniqueCandidate, w.AccountMatch, w.AccountMismatch);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
            return $"{CodeVersion}-{hash}";
        }
    }
}

/// <summary>
/// Reconciliation:Weights. Confidence = sum of the weights of the signals present, clamped to 0..100.
/// Calibrated so that: number + amount (95), amount + date window + the only candidate (70), number + partial amount (75)
/// and a unique exact combination (70) pass the default threshold of 70, while amount + date alone (45) and ambiguous
/// candidates do not.
/// </summary>
public sealed class ReconciliationWeights
{
    /// <summary>The document number is found in the purpose.</summary>
    public int DocumentNumber { get; set; } = 50;

    /// <summary>The contract number of the document is found in the purpose.</summary>
    public int ContractNumber { get; set; } = 15;

    /// <summary>1:1 amounts are equal (within the tolerance).</summary>
    public int Amount { get; set; } = 35;

    /// <summary>The amounts of a combination add up exactly.</summary>
    public int AmountSum { get; set; } = 35;

    /// <summary>Less paid than documented (only together with a number reference).</summary>
    public int PartialAmount { get; set; } = 15;

    /// <summary>More paid than documented (reason Overpaid).</summary>
    public int Overpaid { get; set; } = 10;

    /// <summary>Payment date within the window around the document date.</summary>
    public int DateInWindow { get; set; } = 10;

    /// <summary>The document date is written in the purpose.</summary>
    public int DocumentDateInPurpose { get; set; } = 5;

    /// <summary>No other candidate of the same amount on either side.</summary>
    public int UniqueCandidate { get; set; } = 25;

    /// <summary>Incoming document: the payee account equals the document's sellerAccount.</summary>
    public int AccountMatch { get; set; } = 5;

    /// <summary>Incoming document: the payee account differs from sellerAccount (a warning, not a veto).</summary>
    public int AccountMismatch { get; set; } = -15;
}
