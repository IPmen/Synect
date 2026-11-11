namespace Synect.Application.Attention;

/// <summary>
/// Attention section: thresholds of the attention rules. Every default is an ASSUMPTION to be checked on real data
/// (see the README).
/// </summary>
public sealed class AttentionOptions
{
    /// <summary>Evaluate the rules after every load, reconciliation decision and periodically (PostgreSQL only).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>connection.stale_data: the last successful load of an active connection is older than this.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(36);

    /// <summary>bank.balance_mismatch: closed bank days of this many recent days are checked.</summary>
    public int BalanceCheckDays { get; set; } = 60;

    /// <summary>bank.failed_operations: operations rejected by the bank in this many recent days.</summary>
    public int FailedOperationsDays { get; set; } = 7;

    /// <summary>bank.idle_account: no error-free movement for more than this many days.</summary>
    public int IdleAccountDays { get; set; } = 90;

    /// <summary>
    /// reconciliation.large_unmatched_payment: payments of at least this amount in stored minor units (default
    /// 50 000 000.00 UZS). The Kapital Bank amount unit is NOT confirmed (Adapters:KapitalBank:AmountUnit), so the
    /// comparison may be off by a factor of 100.
    /// </summary>
    public long LargePaymentMinor { get; set; } = 5_000_000_000;

    /// <summary>reconciliation.large_unmatched_payment: the payment has had no matched document for more than this many days.</summary>
    public int UnmatchedGraceDays { get; set; } = 7;

    /// <summary>reconciliation.signed_unpaid_act: the signed act is older than this many days and not fully paid.</summary>
    public int UnpaidActDays { get; set; } = 30;

    /// <summary>Reconciliation rules look only at payments and documents of this many recent days.</summary>
    public int LookbackDays { get; set; } = 180;

    /// <summary>documents.awaiting_our_signature: incoming documents waiting for our signature for more than this many days.</summary>
    public int AwaitingOurSignatureDays { get; set; } = 5;

    /// <summary>documents.awaiting_partner_signature: outgoing documents waiting for the partner for more than this many days.</summary>
    public int AwaitingPartnerSignatureDays { get; set; } = 14;

    /// <summary>documents.rejected_recently: documents rejected within this many days.</summary>
    public int RejectedRecentDays { get; set; } = 7;

    /// <summary>
    /// Rules that report single cases (possible double payments, other accounts, balance mismatches per account) make one
    /// grouped item instead when they find more than this many cases.
    /// </summary>
    public int GroupAbove { get; set; } = 5;

    /// <summary>Grouped items list at most this many entries in details (the count covers all of them).</summary>
    public int MaxListedEntries { get; set; } = 20;

    /// <summary>Time-based rules (stale data, waiting documents, snooze end) are also evaluated this often per client.</summary>
    public TimeSpan PeriodicInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The "N new items need attention" note is added at most once per API key in this interval.</summary>
    public TimeSpan ReminderInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A Dismissed item whose condition has not been found for this many days becomes Archived (hidden from the default
    /// lists; its history stays).
    /// </summary>
    public int DismissedRetentionDays { get; set; } = 90;

    /// <summary>connection.stale_data for file import connections: the last import is older than this.</summary>
    public TimeSpan FileStaleAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Longest snooze.</summary>
    public int MaxSnoozeDays { get; set; } = 90;
}
