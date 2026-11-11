using Synect.Domain.Common;

namespace Synect.Domain.Attention;

/// <summary>How urgent an attention item is. The numeric value is the sort rank (Critical first).</summary>
public enum AttentionSeverity
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4,
}

public enum AttentionCategory
{
    /// <summary>Connections to sources: broken, stale or incomplete loads.</summary>
    Connection,

    /// <summary>Bank data: day balances, failed operations, idle accounts.</summary>
    Bank,

    /// <summary>Reconciliation of payments with documents: possible double payments, other accounts, large unmatched payments, unpaid acts.</summary>
    Reconciliation,

    /// <summary>Didox documents: waiting for a signature, rejected.</summary>
    Documents,
}

public enum AttentionStatus
{
    /// <summary>Open and not looked at yet.</summary>
    New,

    /// <summary>Open, the accountant has looked at it.</summary>
    Seen,

    /// <summary>Hidden until <see cref="AttentionItem.SnoozedUntil"/>; back to New after that if the condition still holds.</summary>
    Snoozed,

    /// <summary>The accountant decided it needs nothing; stays dismissed until its significant content changes.</summary>
    Dismissed,

    /// <summary>The condition is gone (set by the platform); opened again (Reopened) if the condition comes back.</summary>
    Resolved,

    /// <summary>
    /// A Dismissed item whose condition has not been found for Attention:DismissedRetentionDays (set by the platform). Hidden
    /// from the default lists; its events stay. Opened again as New (event Reopened) if the condition comes back.
    /// </summary>
    Archived,
}

public enum AttentionEventKind
{
    Created,
    Reopened,
    Seen,
    Snoozed,
    Dismissed,
    AutoResolved,

    /// <summary>The platform archived a dismissed item whose condition stayed gone (Attention:DismissedRetentionDays).</summary>
    Archived,
}

/// <summary>
/// One case that needs the accountant's attention (attention_items), produced by the platform's rules after loads and
/// reconciliation. The same case (same <see cref="DedupKey"/>) is never created twice: its row is updated, resolved when
/// the condition is gone and reopened when it comes back.
/// </summary>
public class AttentionItem : TenantEntity
{
    public const int MaxCommentLength = 1000;
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 1000;

    public string RuleCode { get; set; } = string.Empty;
    public AttentionCategory Category { get; set; }
    public AttentionSeverity Severity { get; set; }

    /// <summary>Short English title; the model translates it for the user.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Short English explanation with the basis of the finding.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Identifiers and amounts only (JSON object), e.g. {"count": 3, "items": [...]}.</summary>
    public string Details { get; set; } = "{}";

    /// <summary>What the item is about: connection, bank_account, transaction, document, counterparty_inn, tenant.</summary>
    public string EntityKind { get; set; } = string.Empty;

    /// <summary>Id or INN of that entity (empty for tenant-wide grouped items).</summary>
    public string EntityRef { get; set; } = string.Empty;

    /// <summary>Rule code plus the significant parameters (e.g. ids and amount): unique per tenant among non-resolved items.</summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// Grouped items: keys of every member (e.g. document ids). A dismissed grouped item opens again only when a member
    /// appears that was not there when it was dismissed. Empty for single items.
    /// </summary>
    public string[] MemberKeys { get; set; } = [];

    public AttentionStatus Status { get; set; } = AttentionStatus.New;
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Last evaluation that still found the condition.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>API key of the last person decision (seen, snooze, dismiss, reopen); null for platform changes.</summary>
    public Guid? DecidedByKey { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Snoozed items whose time is over count as New (the next evaluation stores it).</summary>
    public AttentionStatus EffectiveStatus(DateTimeOffset now) =>
        Status == AttentionStatus.Snoozed && SnoozedUntil is { } until && until <= now ? AttentionStatus.New : Status;
}

/// <summary>History of an attention item (attention_item_events). Append-only.</summary>
public class AttentionItemEvent : TenantEntity
{
    public Guid ItemId { get; set; }
    public AttentionEventKind Kind { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>API key of the person (null for platform events such as Created or AutoResolved).</summary>
    public Guid? ApiKeyId { get; set; }

    public string? Comment { get; set; }
}
