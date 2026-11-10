using Synect.Domain.Common;
using Synect.Domain.Edo;

namespace Synect.Domain.Reconciliation;

public enum ReconciliationScopeKind
{
    /// <summary>Every counterparty of the tenant.</summary>
    Full,

    /// <summary>Only the listed INNs (whole history of each).</summary>
    Inns,
}

/// <summary>One recalculation of the reconciliation (reconciliation_runs).</summary>
public class ReconciliationRun : TenantEntity
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ReconciliationScopeKind Scope { get; set; }

    /// <summary>The INNs of an <see cref="ReconciliationScopeKind.Inns"/> run (empty string = payments without an INN); null for a full run.</summary>
    public string[]? ScopeInns { get; set; }

    /// <summary>Proposed matches that exist for the scope after the run (new and kept).</summary>
    public int MatchCount { get; set; }

    public int CreatedCount { get; set; }
    public int RemovedCount { get; set; }

    /// <summary>Rule set version: code version plus a hash of the Reconciliation:* settings.</summary>
    public string RulesVersion { get; set; } = string.Empty;
}

public enum ReconciliationMatchType
{
    /// <summary>One payment, one document, equal amounts.</summary>
    Exact,

    /// <summary>One payment closes several documents of one INN.</summary>
    OneToMany,

    /// <summary>Several payments cover one document.</summary>
    ManyToOne,

    /// <summary>Payments and documents differ in total: less paid than documented, or more (reason Overpaid).</summary>
    Partial,
}

public enum MatchStatus
{
    /// <summary>Found by the rules; recalculated on every run.</summary>
    Proposed,

    /// <summary>Accepted by a person; never changed or removed by recalculation (a person may still reject it later).</summary>
    Confirmed,

    /// <summary>Declined by a person; kept, and its payment-document pairs are never proposed again (a person may still confirm it later).</summary>
    Rejected,
}

/// <summary>A possible match between bank payments and Didox documents of one counterparty (reconciliation_matches).</summary>
public class ReconciliationMatch : TenantEntity
{
    public const int MaxCommentLength = 1000;

    public Guid RunId { get; set; }
    public ReconciliationMatchType MatchType { get; set; }
    public MatchStatus Status { get; set; }

    /// <summary>0..100, the sum of the signal weights (see the README).</summary>
    public int Confidence { get; set; }

    /// <summary>JSON array of reasons: [{"code": "DocumentNumberInPurpose", "weight": 50, "params": {...}}, ...].</summary>
    public string Reasons { get; set; } = "[]";

    public string CounterpartyInn { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    /// <summary>Direction of the documents: Outgoing (we sell, money comes in) or Incoming (we buy, money goes out).</summary>
    public EdoDocumentOwner DocumentOwner { get; set; }

    /// <summary>Totals of the matched payments and documents (full amounts, minor units).</summary>
    public long PaymentsTotal { get; set; }
    public long DocumentsTotal { get; set; }

    /// <summary>Hash of the type, items and allocations: a recalculation that finds the same match keeps this row.</summary>
    public string Signature { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedByKey { get; set; }
    public string? DecisionComment { get; set; }
}

public enum MatchItemKind
{
    BankTransaction,
    EdoDocument,
}

/// <summary>A payment or a document taking part in a match, with the amount allocated to it (reconciliation_match_items).</summary>
public class ReconciliationMatchItem : TenantEntity
{
    public Guid MatchId { get; set; }
    public MatchItemKind Kind { get; set; }

    /// <summary>bank_transactions.id or edo_documents.id.</summary>
    public Guid RefId { get; set; }

    public long AllocatedAmountMinor { get; set; }

    /// <summary>Copy of the match status: a partial unique index keeps a payment or document in at most one Proposed match.</summary>
    public MatchStatus MatchStatus { get; set; }
}

public enum FindingKind
{
    /// <summary>An unmatched payment or document and its closest candidate below the threshold.</summary>
    NearestCandidate,

    /// <summary>A payment that may repeat an earlier one (related = the earlier payment).</summary>
    PossibleDuplicate,

    /// <summary>A payment whose purpose names neither a document nor a contract number.</summary>
    NoReference,
}

/// <summary>
/// Derived facts of the last recalculation per counterparty (reconciliation_findings): replaced for an INN every time
/// that INN is recalculated. Never decided by people.
/// </summary>
public class ReconciliationFinding : TenantEntity
{
    public Guid RunId { get; set; }
    public FindingKind Kind { get; set; }

    /// <summary>Empty string for payments without an INN.</summary>
    public string CounterpartyInn { get; set; } = string.Empty;

    public MatchItemKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public MatchItemKind? RelatedKind { get; set; }
    public Guid? RelatedId { get; set; }
    public int Confidence { get; set; }

    /// <summary>JSON array of reasons, same shape as <see cref="ReconciliationMatch.Reasons"/>.</summary>
    public string Reasons { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One decision of a person on a match (reconciliation_match_decisions): the first confirm/reject of a Proposed match
/// and every later change between Confirmed and Rejected. Append-only; the match keeps only the latest decision.
/// </summary>
public class ReconciliationMatchDecision : TenantEntity
{
    public Guid MatchId { get; set; }
    public MatchStatus PreviousStatus { get; set; }
    public MatchStatus NewStatus { get; set; }
    public DateTimeOffset DecidedAt { get; set; }

    /// <summary>API key of the caller (null when decided outside MCP, e.g. from DevTools).</summary>
    public Guid? DecidedByKey { get; set; }

    public string? Comment { get; set; }
}
