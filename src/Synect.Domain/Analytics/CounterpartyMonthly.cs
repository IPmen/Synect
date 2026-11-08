using Synect.Domain.Common;

namespace Synect.Domain.Analytics;

/// <summary>
/// Precomputed turnover with one counterparty in one calendar month and currency
/// (analytics_counterparty_monthly). Rebuilt after each successful load for the months and INNs the load
/// touched. Amounts are in minor units; bank operations with errors are excluded; EDO totals count only
/// Analytics:Edo:CountedStatuses and use the document date.
/// </summary>
public class CounterpartyMonthly : TenantEntity
{
    /// <summary>Empty string groups payments without an INN.</summary>
    public string CounterpartyInn { get; set; } = string.Empty;

    /// <summary>First day of the month.</summary>
    public DateOnly Month { get; set; }

    public string Currency { get; set; } = string.Empty;

    public long BankIncoming { get; set; }
    public long BankOutgoing { get; set; }
    public int BankCount { get; set; }

    public long EdoIncomingTotal { get; set; }
    public long EdoOutgoingTotal { get; set; }
    public int EdoIncomingCount { get; set; }
    public int EdoOutgoingCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool SameValuesAs(CounterpartyMonthly other) =>
        BankIncoming == other.BankIncoming && BankOutgoing == other.BankOutgoing && BankCount == other.BankCount
        && EdoIncomingTotal == other.EdoIncomingTotal && EdoOutgoingTotal == other.EdoOutgoingTotal
        && EdoIncomingCount == other.EdoIncomingCount && EdoOutgoingCount == other.EdoOutgoingCount;
}
