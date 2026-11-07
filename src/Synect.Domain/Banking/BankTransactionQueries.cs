namespace Synect.Domain.Banking;

public static class BankTransactionQueries
{
    /// <summary>
    /// Operations that actually moved money: excludes operations the bank flagged with an error.
    /// Use this for balances and reconciliation.
    /// </summary>
    public static IQueryable<BankTransaction> WithoutErrors(this IQueryable<BankTransaction> source) =>
        source.Where(t => !t.HasError);

    public static IEnumerable<BankTransaction> WithoutErrors(this IEnumerable<BankTransaction> source) =>
        source.Where(t => !t.HasError);
}
