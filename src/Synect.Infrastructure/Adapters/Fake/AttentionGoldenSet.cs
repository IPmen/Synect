using Synect.Application.Sync;
using Synect.Domain.Attention;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>An attention item the golden set must produce: rule, severity and number of cases in it.</summary>
public sealed record ExpectedAttention(string Case, string RuleCode, AttentionSeverity Severity, int Cases);

/// <summary>A rule that must produce nothing on the golden set, and why.</summary>
public sealed record ExpectedNoAttention(string RuleCode, string Why);

/// <summary>
/// Deterministic data set for the attention rules, loaded through the fake adapters and the real sync services: the
/// reconciliation golden set (double payment p10b, payment to another account p11) plus a broken connection, stale data,
/// a day whose balance does not add up, an operation rejected by the bank, an idle account, a large payment without a
/// document, documents waiting for a signature and a rejected document. Times are fixed: the stale connection is loaded
/// at <see cref="StaleLoadAt"/>, everything else at <see cref="EvaluateAt"/>.
/// </summary>
public static class AttentionGoldenSet
{
    public const string TenantName = "Attention golden set";

    public static readonly DateTimeOffset StaleLoadAt = new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(5));
    public static readonly DateTimeOffset EvaluateAt = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(5));
    public static readonly DateOnly IdleDay = new(2026, 7, 1);
    public static readonly DateOnly BrokenBalanceDay = new(2026, 10, 21);

    public const string LargePaymentInn = "309000001";
    public const long LargePaymentAmount = 60_000_000_00;

    public sealed record Connections(Guid Bank, Guid Edo, Guid BrokenBank, Guid StaleEdo);

    public static IReadOnlyList<ExpectedAttention> Expected { get; } =
    [
        new("broken connection", Application.Attention.AttentionRuleCodes.ConnectionNeedsAttention, AttentionSeverity.Critical, 1),
        new("stale data (loaded 48 hours earlier)", Application.Attention.AttentionRuleCodes.ConnectionStaleData, AttentionSeverity.High, 1),
        new("day balance does not add up (2026-10-21)", Application.Attention.AttentionRuleCodes.BankBalanceMismatch, AttentionSeverity.High, 1),
        new("operation rejected by the bank (2026-10-31)", Application.Attention.AttentionRuleCodes.BankFailedOperations, AttentionSeverity.Medium, 1),
        new("idle account (last movement 2026-07-01)", Application.Attention.AttentionRuleCodes.BankIdleAccounts, AttentionSeverity.Low, 1),
        new("double payment (p10b repeats p10a)", Application.Attention.AttentionRuleCodes.PossibleDuplicatePayment, AttentionSeverity.High, 1),
        new("payment to another account (p11)", Application.Attention.AttentionRuleCodes.PaymentAccountMismatch, AttentionSeverity.High, 1),
        new("large payment without a document (60 000 000.00)", Application.Attention.AttentionRuleCodes.LargeUnmatchedPayments, AttentionSeverity.Medium, 1),
        new("document waits for our signature 11 days", Application.Attention.AttentionRuleCodes.AwaitingOurSignature, AttentionSeverity.Medium, 1),
        new("document waits for the partner 26 days", Application.Attention.AttentionRuleCodes.AwaitingPartnerSignature, AttentionSeverity.Low, 1),
        new("document rejected yesterday", Application.Attention.AttentionRuleCodes.RejectedRecently, AttentionSeverity.Medium, 1),
    ];

    public static IReadOnlyList<ExpectedNoAttention> ExpectedNotCreated { get; } =
    [
        new(Application.Attention.AttentionRuleCodes.ConnectionIncompleteLoad, "every bank day older than yesterday is closed and the Didox loads read every page"),
        new(Application.Attention.AttentionRuleCodes.SignedUnpaidActs, "the signed act АКТ-5 is unpaid but only 16 days old (threshold 30 days)"),
    ];

    /// <summary>Cases that must NOT be inside the produced items (below a threshold).</summary>
    public static IReadOnlyList<string> NotListed { get; } =
    [
        "w2: incoming document waiting 2 days (threshold 5)",
        "r2: document rejected 35 days ago (window 7 days)",
        "p1..p19: payments below Attention:LargePaymentMinor",
    ];

    private static readonly IReadOnlyList<FakeBankOperation> ExtraOperations =
    [
        new() { Key = "a_large", Day = new DateOnly(2026, 10, 20), Dir = 2, Amount = LargePaymentAmount, Inn = LargePaymentInn, Name = "TEXNIKA IMPORT", Purpose = "Оплата за оборудование" },
        new() { Key = "a_failed", Day = new DateOnly(2026, 10, 31), Dir = 2, Amount = 777_00, Inn = "309000003", Name = "XIZMAT", Purpose = "Оплата услуг", Err = 5 },
        new() { Key = "a_idle", AccountIndex = 2, Day = IdleDay, Dir = 1, Amount = 10_000_00, Inn = "309000002", Name = "OLD PAYER", Purpose = "Возврат" },
    ];

    private static FakeDidoxDocument Doc(string id, int owner, int status, string number, DateOnly date, string inn, decimal total, DateTimeOffset updated) => new()
    {
        DocId = id, DocType = "002", Owner = owner, Status = status, Name = number, DocDate = date, PartnerTin = inn,
        PartnerCompany = "PARTNER " + inn, TotalSum = total, TotalVatSum = Math.Round(total * 12m / 112m, 2),
        Created = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(5)), Updated = updated,
    };

    private static DateTimeOffset Local(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(5));

    private static IReadOnlyList<FakeDidoxDocument> ExtraDocuments() =>
    [
        Doc("w1", 0, 2, "W-1", new DateOnly(2026, 10, 25), "309000011", 15_000m, Local(10, 25)),
        Doc("w2", 0, 2, "W-2", new DateOnly(2026, 11, 3), "309000012", 16_000m, Local(11, 3)),
        Doc("w3", 1, 1, "W-3", new DateOnly(2026, 10, 10), "309000013", 17_000m, Local(10, 10)),
        Doc("r1", 1, 4, "R-1", new DateOnly(2026, 10, 28), "309000014", 18_000m, Local(11, 4, 10)),
        Doc("r2", 1, 4, "R-2", new DateOnly(2026, 9, 25), "309000015", 19_000m, Local(10, 1)),
    ];

    /// <summary>Configures the fake sources of the four connections.</summary>
    public static void Apply(FakeBankDataSource bank, FakeEdoDataSource edo, Connections c)
    {
        var b = bank.For(c.Bank);
        var e = edo.For(c.Edo);
        ReconciliationGoldenSet.Apply(b, e);
        b.AccountCount = 3;
        b.AccountCurrencies = ["000", "840", "000"];
        b.FixedOperations.AddRange(ExtraOperations);
        b.BrokenBalanceDays.Add(BrokenBalanceDay);
        e.UseDocuments(ReconciliationGoldenSet.Documents().Concat(ExtraDocuments()));

        var broken = bank.For(c.BrokenBank);
        broken.Seed = 778;
        broken.AuthFailure = true;
        var stale = edo.For(c.StaleEdo);
        stale.Seed = 779;
        stale.UseDocuments([]);
    }

    /// <summary>
    /// Loads the set through the sync services: the stale Didox connection at <see cref="StaleLoadAt"/>, then (at
    /// <see cref="EvaluateAt"/>) the bank and Didox data and a load of the broken bank connection, which fails on
    /// authentication and puts the connection into NeedsAttention.
    /// </summary>
    public static async Task<IReadOnlyList<SyncOutcome>> LoadAsync(
        Connections c, Action<DateTimeOffset> setClock,
        Func<Guid, DateOnly, DateOnly, Task<SyncOutcome>> syncBank, Func<Guid, Task<SyncOutcome>> syncEdo)
    {
        var outcomes = new List<SyncOutcome>();
        setClock(StaleLoadAt);
        outcomes.Add(await syncEdo(c.StaleEdo));
        setClock(EvaluateAt);
        outcomes.Add(await syncBank(c.Bank, ReconciliationGoldenSet.BankFrom, ReconciliationGoldenSet.BankTo));
        outcomes.Add(await syncBank(c.Bank, IdleDay, IdleDay));
        outcomes.Add(await syncEdo(c.Edo));
        var broken = await syncBank(c.BrokenBank, ReconciliationGoldenSet.BankTo, ReconciliationGoldenSet.BankTo);
        if (broken.Result != SyncRunResult.Failed) throw new InvalidOperationException("The broken connection was expected to fail.");
        foreach (var o in outcomes.Where(o => o.Result != SyncRunResult.Succeeded))
            throw new InvalidOperationException($"Golden set load failed: {o.Error}");
        outcomes.Add(broken);
        return outcomes;
    }
}
