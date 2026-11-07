using Synect.Domain.Common;

namespace Synect.Domain.Banking;

/// <summary>Daily totals for an account. All amounts are in minor currency units.</summary>
public class BankDailyBalance : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public DateOnly Date { get; set; }
    public long OpeningBalance { get; set; }
    public long ClosingBalance { get; set; }

    /// <summary>Total outflow for the day.</summary>
    public long TotalDebit { get; set; }

    /// <summary>Total inflow for the day.</summary>
    public long TotalCredit { get; set; }

    /// <summary>True when the bank reports the operational day as closed.</summary>
    public bool IsDayClosed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
