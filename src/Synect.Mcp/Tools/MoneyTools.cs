using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

/// <summary>Block A: money on accounts and cash flows. One analytics method per tool.</summary>
[McpServerToolType]
public sealed class MoneyTools(ToolRunner runner, Presenter p, BalancesAnalytics balances, FlowsAnalytics flows, IntegrityAnalytics integrity)
{
    private const ToolGroup Group = ToolGroup.Bank;

    [McpServerTool(Name = "get_account_balances", Title = "Account balances", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Closing balance of every bank account and totals per currency: the latest known balance, or at the end of a given date (A1, A2). " +
                 "Examples: «Сколько денег на счетах?», «Какой был остаток на 1 октября?», «Остаток по долларовому счёту»." + Common)]
    public Task<CallToolResult> GetAccountBalances(
        [Description("Optional date yyyy-MM-dd: balance at the end of that day. Omit for the latest balance.")] string? date = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_account_balances", Group, async (a, ct) =>
        {
            var on = a.OptionalDate("date", date);
            if (on is { } d) a.SetPeriodEcho(new { Date = d });
            var r = on is { } day ? await balances.GetOnDateAsync(day, ct) : await balances.GetCurrentAsync(ct);
            if (on is null) a.SetPeriodEcho(new { Date = r.Data.AsOf, Latest = true });
            var accounts = r.Data.Accounts.Take(runner.Options.MaxLimit).Select(p.AccountBalance).ToList();
            return new ToolOutput(new { r.Data.AsOf, Accounts = accounts, TotalsByCurrency = p.Totals(r.Data.TotalsByCurrency) },
                accounts.Count, r.Freshness, Truncated: r.Data.Accounts.Count > runner.Options.MaxLimit);
        }, cancellationToken);

    [McpServerTool(Name = "get_balance_history", Title = "Daily balance history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Closing balance per account and day over a period; days without bank data carry the previous balance forward (is_carried_forward) (A4). " +
                 "Examples: «Как менялся остаток в октябре?», «Покажи остатки по дням за неделю»." + Common)]
    public Task<CallToolResult> GetBalanceHistory(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("Optional account_id (from get_account_balances) to limit to one account.")] string? account_id = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_balance_history", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var account = a.OptionalGuid("account_id", account_id);
            var r = await balances.GetHistoryAsync(period, account, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.AccountId,
                x.Date,
                Balance = x.Balance is { } b ? p.Money(b, x.Currency) : null,
                x.IsDayClosed,
                x.IsCarriedForward,
                x.SourceDate,
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_balance_stats", Title = "Balance statistics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Average, minimum (with its date) and maximum daily closing balance per account over a period (A11). " +
                 "Examples: «Какой средний остаток за квартал?», «Когда остаток был минимальным?»." + Common)]
    public Task<CallToolResult> GetBalanceStats(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_balance_stats", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var rows = a.Limit(limit);
            var r = await balances.GetStatsAsync(period, ct);
            return ToolOutputs.List(r.Data, rows, r.Freshness, s => new
            {
                s.AccountId,
                AccountNumber = p.Account(s.AccountNumber),
                DaysWithData = s.Days,
                Average = p.Money(s.Average, s.Currency),
                Minimum = p.Money(s.Minimum, s.Currency),
                s.MinimumDate,
                Maximum = p.Money(s.Maximum, s.Currency),
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_cash_flow", Title = "Cash flow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Incoming, outgoing and net money per day, ISO week or month and currency (A3). " +
                 "Examples: «Сколько пришло и ушло денег за октябрь?», «Движение денег по неделям», «Чистый поток за год по месяцам»." + Common)]
    public Task<CallToolResult> GetCashFlow(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("Bucket size: day, week or month (default month).")] string granularity = "month",
        [Description("Optional account_id to limit to one account.")] string? account_id = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_cash_flow", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var g = a.Enum("granularity", granularity, ToolEnums.Granularity);
            var account = a.OptionalGuid("account_id", account_id);
            var r = await flows.GetCashFlowAsync(period, g, account, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, b => new
            {
                b.BucketStart,
                Incoming = p.Money(b.Incoming, b.Currency),
                Outgoing = p.Money(b.Outgoing, b.Currency),
                Net = p.Money(b.Net, b.Currency),
                OperationCount = b.Count,
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_largest_transactions", Title = "Largest bank operations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Largest bank operations of a period, per currency by amount descending, optionally only incoming or outgoing (A5). " +
                 "Examples: «Самые крупные платежи за месяц», «Топ-10 поступлений в долларах»." + Common)]
    public Task<CallToolResult> GetLargestTransactions(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("Optional: incoming or outgoing.")] string? direction = null,
        [Description("Optional currency code exactly as shown in other answers (e.g. UZS, USD or a numeric code such as 840).")] string? currency = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_largest_transactions", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var dir = a.OptionalEnum("direction", direction, ToolEnums.Direction);
            var cur = a.OptionalCurrency("currency", currency);
            var r = await flows.GetLargestAsync(period, dir, cur, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, p.Operation);
        }, cancellationToken);

    [McpServerTool(Name = "get_transactions_for_day", Title = "Bank operations of a day", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("All bank operations of one day with their status (final, bank state, day closed) (A6). " +
                 "Examples: «Что прошло по счёту вчера?», «Платежи за 3 октября», «Прошёл ли платёж сегодня?»." + Common)]
    public Task<CallToolResult> GetTransactionsForDay(
        [Description("The day, yyyy-MM-dd (local date).")] string date,
        [Description("Optional account_id to limit to one account.")] string? account_id = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_transactions_for_day", Group, async (a, ct) =>
        {
            var day = a.Date("date", date);
            a.SetPeriodEcho(new { Date = day });
            var account = a.OptionalGuid("account_id", account_id);
            var r = await flows.GetDayAsync(day, account, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, p.Operation);
        }, cancellationToken);

    [McpServerTool(Name = "find_idle_accounts", Title = "Idle accounts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Accounts without any operation for more than the given number of days (A9). " +
                 "Examples: «Какие счета не используются?», «По каким счетам не было движения 90 дней?»." + Common)]
    public Task<CallToolResult> FindIdleAccounts(
        [Description("Days without operations, 1-3650 (default 30).")] int? days = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("find_idle_accounts", Group, async (a, ct) =>
        {
            var d = a.Int("days", days, 30, 1, 3650);
            var r = await flows.GetIdleAccountsAsync(d, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.AccountId,
                AccountNumber = p.Account(x.AccountNumber),
                AccountName = p.Untrusted(x.AccountName),
                x.Currency,
                x.LastMovementDate,
                x.DaysIdle,
            });
        }, cancellationToken);

    [McpServerTool(Name = "check_balance_integrity", Title = "Balance integrity check", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Checks stored bank days of a period: opening + credit - debit = closing, carry-over to the next day, missing days, and day totals " +
                 "against stored operations. Returns only discrepancies (A7, A10). " +
                 "Examples: «Сходятся ли остатки?», «Есть ли пропущенные дни в выписке?», «Проверь целостность данных банка за октябрь»." + Common)]
    public Task<CallToolResult> CheckBalanceIntegrity(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("Optional account_id to limit to one account.")] string? account_id = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("check_balance_integrity", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var account = a.OptionalGuid("account_id", account_id);
            var r = await integrity.CheckBalancesAsync(period, account, a.Page(limit, cursor), ct);
            var notes = r.Data.Items.Count == 0 && r.Data.NextCursor is null
                ? new[] { "No discrepancies were found among the stored bank days of the period." }
                : null;
            return ToolOutputs.Page(r, x => new
            {
                x.AccountId,
                AccountNumber = p.Account(x.AccountNumber),
                x.Date,
                x.Kind,
                Expected = x.Kind == BalanceDiscrepancyKind.MissingDays ? null : p.Money(x.Expected, x.Currency),
                Actual = x.Kind == BalanceDiscrepancyKind.MissingDays ? null : p.Money(x.Actual, x.Currency),
                DaysToNextStoredDay = x.Kind == BalanceDiscrepancyKind.MissingDays ? x.Actual : (long?)null,
                x.IsDayClosed,
                x.NextDate,
            }, notes);
        }, cancellationToken);
}
