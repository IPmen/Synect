using System.Text.Json;
using Synect.Application.Accounting1C;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Postgres;
using static Synect.Infrastructure.Adapters.Fake.ReconciliationGoldenSet;

namespace Synect.Tests.Accounting1C;

/// <summary>The 1C questions through the real MCP transport on the 1C golden set (known right answers).</summary>
public class AccountingToolsTests(AccountingFixture f) : IClassFixture<AccountingFixture>
{
    private static readonly object October = new { from = "2026-10-01", to = "2026-10-31" };

    public static readonly string[] Tools =
    [
        "get_1c_documents", "get_1c_payment_orders", "get_1c_settlement_balances", "get_1c_bank_account_balances",
        "compare_bank_with_1c", "compare_didox_with_1c", "compare_bank_balances_with_1c", "compare_counterparty_with_1c",
    ];

    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static JsonElement Data(JsonElement body) => body.GetProperty("data");

    private static List<JsonElement> List(JsonElement element) => element.EnumerateArray().ToList();

    private static long Minor(JsonElement money) => money.GetProperty("amount_minor").GetInt64();

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>(kind, key) of a difference row: the 1C ref for 1C-only rows, else the other side's key.</summary>
    private static (string Kind, string Key) Difference(JsonElement row, string sourceProperty, Func<JsonElement, string> sourceKey)
    {
        var source = row.GetProperty(sourceProperty);
        return (row.GetProperty("kind").GetString()!,
            source.ValueKind == JsonValueKind.Null ? row.GetProperty("accounting_document").GetProperty("ref").GetString()! : sourceKey(source));
    }

    private static string BankKey(JsonElement payment) => $"{Minor(payment.GetProperty("amount"))}|{Text(payment, "counterparty_inn")}";

    [Fact]
    public async Task Bank_statement_against_1c_finds_exactly_the_planted_differences()
    {
        RequirePostgres();
        var body = await f.AskAsync(f.GoldenKey, "compare_bank_with_1c", October);
        var summary = Data(body).GetProperty("summary");
        Assert.Equal(Accounting1CGoldenSet.MatchedPayments, summary.GetProperty("matched_without_difference").GetInt32());
        Assert.Equal(4, summary.GetProperty("total_differences").GetInt32());

        var rows = List(Data(body).GetProperty("differences"));
        Assert.Equal(
        [
            ("amount_differs", $"50000000|{A}"), ("missing_in_1c", $"33333333|{C}"), ("missing_in_bank", "bd-extra-1"), ("not_posted_in_1c", $"55500000|{H}"),
        ], rows.Select(r => Difference(r, "bank_payment", BankKey)).Order().ToList());

        var differs = rows.Single(r => r.GetProperty("kind").GetString() == "amount_differs");
        Assert.Equal(500_000, Minor(differs.GetProperty("amount_difference")));
        Assert.Equal(50_500_000, Minor(differs.GetProperty("accounting_document").GetProperty("amount")));
        Assert.Equal("INN and date", differs.GetProperty("matched_by").GetString());
        var unposted = rows.Single(r => r.GetProperty("kind").GetString() == "not_posted_in_1c");
        Assert.False(unposted.GetProperty("accounting_document").GetProperty("posted").GetBoolean());
        // The payment entered a day later in 1C (p8b) and the one without an INN (p12) are found, not reported.
        Assert.DoesNotContain(rows, r => r.GetProperty("bank_payment").ValueKind != JsonValueKind.Null
                                         && Minor(r.GetProperty("bank_payment").GetProperty("amount")) is 45_000_000 or 120_000_000);
        // Summary totals are per currency.
        var missing = List(summary.GetProperty("differences")).Single(d => d.GetProperty("kind").GetString() == "missing_in_1c");
        Assert.Equal(33_333_333, Minor(List(missing.GetProperty("totals")).Single()));
    }

    [Fact]
    public async Task Didox_against_1c_finds_exactly_the_planted_differences()
    {
        RequirePostgres();
        var body = await f.AskAsync(f.GoldenKey, "compare_didox_with_1c", October);
        var summary = Data(body).GetProperty("summary");
        Assert.Equal(Accounting1CGoldenSet.MatchedDocuments, summary.GetProperty("matched_without_difference").GetInt32());

        var rows = List(Data(body).GetProperty("differences"));
        Assert.Equal(
            [("amount_differs", "d9"), ("missing_in_1c", "d7c"), ("missing_in_didox", "doc-extra-1"), ("not_posted_in_1c", "d16h")],
            rows.Select(r => Difference(r, "didox_document", d => d.GetProperty("external_id").GetString()!)).Order().ToList());
        var differs = rows.Single(r => r.GetProperty("kind").GetString() == "amount_differs");
        Assert.Equal(1_000_000, Minor(differs.GetProperty("amount_difference")));
        Assert.Equal("Didox id (esf_id)", differs.GetProperty("matched_by").GetString());
        // The draft (d14) and the contract (d19) are not accounting documents to compare; the deleted 1C document is ignored.
        // (Checked on the document identifiers of the rows, not on the raw text: ids such as "d14" can occur inside a UUID.)
        var didoxIds = rows.Select(r => r.GetProperty("didox_document")).Where(d => d.ValueKind != JsonValueKind.Null)
            .Select(d => d.GetProperty("external_id").GetString()).ToList();
        var accountingRefs = rows.Select(r => r.GetProperty("accounting_document")).Where(d => d.ValueKind != JsonValueKind.Null)
            .Select(d => d.GetProperty("ref").GetString()).ToList();
        Assert.DoesNotContain("d14", didoxIds);
        Assert.DoesNotContain("d19", didoxIds);
        Assert.DoesNotContain("doc-deleted-1", accountingRefs);
        Assert.DoesNotContain("doc-d14", accountingRefs);
        Assert.DoesNotContain("doc-d19", accountingRefs);
    }

    [Fact]
    public async Task Kinds_filter_and_paging_walk_the_same_rows()
    {
        RequirePostgres();
        var only = await f.AskAsync(f.GoldenKey, "compare_bank_with_1c", new { from = "2026-10-01", to = "2026-10-31", kinds = "missing_in_1c,missing_in_source" });
        Assert.Equal(["missing_in_1c", "missing_in_bank"], List(Data(only).GetProperty("differences")).Select(r => r.GetProperty("kind").GetString()));
        // The summary still counts every kind.
        Assert.Equal(4, Data(only).GetProperty("summary").GetProperty("total_differences").GetInt32());

        var kinds = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 6; i++)
        {
            var page = await f.AskAsync(f.GoldenKey, "compare_didox_with_1c", new { from = "2026-10-01", to = "2026-10-31", limit = 1, cursor });
            kinds.AddRange(List(Data(page).GetProperty("differences")).Select(r => r.GetProperty("kind").GetString()!));
            cursor = Text(page, "next_cursor");
            if (cursor is null) break;
        }
        Assert.Equal(["missing_in_1c", "missing_in_didox", "amount_differs", "not_posted_in_1c"], kinds);

        // A period without the planted payment does not report it.
        var early = await f.AskAsync(f.GoldenKey, "compare_bank_with_1c", new { from = "2026-10-01", to = "2026-10-09" });
        Assert.Equal(0, Data(early).GetProperty("summary").GetProperty("total_differences").GetInt32());
    }

    [Fact]
    public async Task An_unchanged_mirror_agrees_completely()
    {
        RequirePostgres();
        foreach (var tool in new[] { "compare_bank_with_1c", "compare_didox_with_1c" })
        {
            var body = await f.AskAsync(f.CleanKey, tool, October);
            Assert.Equal(0, Data(body).GetProperty("summary").GetProperty("total_differences").GetInt32());
            Assert.Empty(List(Data(body).GetProperty("differences")));
        }
        Assert.Equal(23, Data(await f.AskAsync(f.CleanKey, "compare_bank_with_1c", October)).GetProperty("summary").GetProperty("matched_without_difference").GetInt32());
        Assert.Equal(24, Data(await f.AskAsync(f.CleanKey, "compare_didox_with_1c", October)).GetProperty("summary").GetProperty("matched_without_difference").GetInt32());
        Assert.All(List(Data(await f.AskAsync(f.CleanKey, "compare_bank_balances_with_1c"))), r => Assert.Equal("equal", r.GetProperty("status").GetString()));
        foreach (var inn in new[] { A, B, C, H })
            Assert.True(Data(await f.AskAsync(f.CleanKey, "compare_counterparty_with_1c", new { inn, from = "2026-10-01", to = "2026-10-31" })).GetProperty("agrees").GetBoolean(), inn);
    }

    [Fact]
    public async Task Bank_balances_against_1c_show_the_account_that_differs()
    {
        RequirePostgres();
        var rows = List(Data(await f.AskAsync(f.GoldenKey, "compare_bank_balances_with_1c", new { date = "2026-10-31" })));
        Assert.Equal(2, rows.Count);
        var differs = rows.Single(r => r.GetProperty("status").GetString() == "differs");
        Assert.Equal(Accounting1CGoldenSet.UsdBalanceDifference, Minor(differs.GetProperty("difference")));
        Assert.Equal("USD", differs.GetProperty("difference").GetProperty("currency").GetString());
        Assert.Equal("equal", rows.Single(r => r.GetProperty("status").GetString() != "differs").GetProperty("status").GetString());
        Assert.All(rows, r => Assert.StartsWith("****", r.GetProperty("account_number").GetString()));

        var balances = List(Data(await f.AskAsync(f.GoldenKey, "get_1c_bank_account_balances")));
        Assert.Equal(2, balances.Count);
        Assert.All(balances, b => Assert.Equal("2026-10-31", b.GetProperty("balance_date").GetString()));
        // Before the first 1C export there is nothing to compare with.
        var before = List(Data(await f.AskAsync(f.GoldenKey, "compare_bank_balances_with_1c", new { date = "2026-10-15" })));
        Assert.All(before, r => Assert.Equal("no_1c_balance", r.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Settlements_with_a_counterparty_show_each_turnover_on_both_sides()
    {
        RequirePostgres();
        var data = Data(await f.AskAsync(f.GoldenKey, "compare_counterparty_with_1c", new { inn = C, from = "2026-10-01", to = "2026-10-31" }));
        Assert.False(data.GetProperty("agrees").GetBoolean());
        var lines = List(data.GetProperty("lines")).ToDictionary(l => l.GetProperty("line").GetString()!);
        // Didox: 750 000 + 333 333.33 + 100 000; 1C has 110 000 for the last one.
        Assert.Equal(118_333_333, Minor(lines["sales_documents"].GetProperty("source")));
        Assert.Equal(119_333_333, Minor(lines["sales_documents"].GetProperty("accounting")));
        Assert.Equal(1_000_000, Minor(lines["sales_documents"].GetProperty("difference")));
        Assert.Equal("didox", lines["sales_documents"].GetProperty("compared_with").GetString());
        // Bank: three receipts; the one of 333 333.33 is not entered in 1C.
        Assert.Equal(120_333_333, Minor(lines["receipts"].GetProperty("source")));
        Assert.Equal(3, lines["receipts"].GetProperty("source_count").GetInt32());
        Assert.Equal(87_000_000, Minor(lines["receipts"].GetProperty("accounting")));
        Assert.Equal(2, lines["receipts"].GetProperty("accounting_count").GetInt32());
        Assert.Equal(-33_333_333, Minor(lines["receipts"].GetProperty("difference")));
        // 1C balance: sales 1 193 333.33 minus receipts 870 000.
        var balance = List(data.GetProperty("accounting_balance")).Single();
        Assert.Equal("receivable", balance.GetProperty("side").GetString());
        Assert.Equal(32_333_333, Minor(balance.GetProperty("net")));

        var agrees = Data(await f.AskAsync(f.GoldenKey, "compare_counterparty_with_1c", new { inn = E, from = "2026-10-01", to = "2026-10-31" }));
        Assert.True(agrees.GetProperty("agrees").GetBoolean());
    }

    [Fact]
    public async Task Documents_of_1c_are_listed_by_state_esf_counterparty_and_number()
    {
        RequirePostgres();
        async Task<List<string>> RefsAsync(object args) =>
            List(Data(await f.AskAsync(f.GoldenKey, "get_1c_documents", args))).Select(d => d.GetProperty("ref").GetString()!).Order().ToList();

        Assert.Equal(24, (await RefsAsync(new { limit = 200 })).Count);                      // 23 mirrored + the one only in 1C
        Assert.Equal(["doc-d16h"], await RefsAsync(new { state = "unposted" }));
        Assert.Equal(["doc-deleted-1"], await RefsAsync(new { state = "deleted" }));
        Assert.Equal(25, (await RefsAsync(new { state = "all", limit = 200 })).Count);
        Assert.Equal(["doc-d20", "doc-extra-1"], await RefsAsync(new { without_esf = true }));
        Assert.Equal(["doc-d3", "doc-d4", "doc-d9"], await RefsAsync(new { inn = C }));
        Assert.Equal(["doc-extra-1"], await RefsAsync(new { number = "H-99" }));
        Assert.Equal(["doc-d11", "doc-d20", "doc-d5", "doc-d8"], await RefsAsync(new { kind = "purchase" }));
        // 1-2 October, sales: СФ-101 (d1), 45 (d3), F-1 (d7a), H-1 (d17a); the contract d19 is not a 1C document here.
        Assert.Equal(["doc-d1", "doc-d17a", "doc-d3", "doc-d7a"], await RefsAsync(new { from = "2026-10-01", to = "2026-10-02", kind = "sales" }));

        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await f.AskAsync(f.GoldenKey, "get_1c_documents", new { limit = 10, cursor });
            all.AddRange(List(Data(page)).Select(d => d.GetProperty("ref").GetString()!));
            cursor = Text(page, "next_cursor");
        } while (cursor is not null);
        Assert.Equal(24, all.Distinct().Count());
        Assert.Equal(24, all.Count);

        var doc = List(Data(await f.AskAsync(f.GoldenKey, "get_1c_documents", new { number = "47" }))).Single();
        Assert.Equal(11_000_000, Minor(doc.GetProperty("total")));
        Assert.Equal("Sales", doc.GetProperty("kind").GetString());
        Assert.Equal(C, doc.GetProperty("counterparty_inn").GetString());
        Assert.True(doc.GetProperty("counterparty_name").TryGetProperty("untrusted_text", out _));
    }

    [Fact]
    public async Task Payment_orders_and_settlement_balances_come_from_1c()
    {
        RequirePostgres();
        var orders = List(Data(await f.AskAsync(f.GoldenKey, "get_1c_payment_orders")));
        Assert.Equal(6, orders.Count);
        var prepared = List(Data(await f.AskAsync(f.GoldenKey, "get_1c_payment_orders", new { state = "unposted" }))).Single();
        Assert.Equal("po-extra-1", prepared.GetProperty("ref").GetString());
        Assert.Equal("Подготовлено", prepared.GetProperty("bank_status").GetProperty("untrusted_text").GetString());
        Assert.Equal(15_000_000, Minor(prepared.GetProperty("amount")));

        var report = Data(await f.AskAsync(f.GoldenKey, "get_1c_settlement_balances", new { limit = 200 }));
        Assert.Equal("2026-10-31", report.GetProperty("balance_date").GetString());
        var rows = List(report.GetProperty("counterparties"));
        var c = rows.Single(r => Text(r, "counterparty_inn") == C);
        Assert.Equal("receivable", c.GetProperty("side").GetString());
        Assert.Equal(32_333_333, Minor(c.GetProperty("net")));
        // Totals per currency equal the sums of the rows (receivable and payable apart).
        foreach (var total in List(report.GetProperty("totals")))
        {
            var currency = total.GetProperty("receivable").GetProperty("currency").GetString();
            var sameCurrency = rows.Where(r => r.GetProperty("net").GetProperty("currency").GetString() == currency).ToList();
            Assert.Equal(sameCurrency.Where(r => Minor(r.GetProperty("net")) > 0).Sum(r => Minor(r.GetProperty("net"))), Minor(total.GetProperty("receivable")));
            Assert.Equal(-sameCurrency.Where(r => Minor(r.GetProperty("net")) < 0).Sum(r => Minor(r.GetProperty("net"))), Minor(total.GetProperty("payable")));
        }
        var one = Data(await f.AskAsync(f.GoldenKey, "get_1c_settlement_balances", new { inn = C }));
        Assert.Single(List(one.GetProperty("counterparties")));
        var none = Data(await f.AskAsync(f.GoldenKey, "get_1c_settlement_balances", new { date = "2026-09-30" }));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("balance_date").ValueKind);
    }

    [Fact]
    public async Task Answers_carry_freshness_notes_masked_accounts_and_wrapped_text()
    {
        RequirePostgres();
        foreach (var tool in Tools)
        {
            object? args = tool switch
            {
                "compare_bank_with_1c" or "compare_didox_with_1c" => October,
                "compare_counterparty_with_1c" => new { inn = A, from = "2026-10-01", to = "2026-10-31" },
                _ => null,
            };
            var body = await f.AskAsync(f.GoldenKey, tool, args);
            Assert.Equal("ok", body.GetProperty("status").GetString());
            var sources = List(body.GetProperty("data_freshness").GetProperty("sources"));
            Assert.Contains(sources, s => s.GetProperty("kind").GetString() == "accounting1_c" || s.GetProperty("source").GetString() == "1C");
            var notes = List(body.GetProperty("notes")).Select(n => n.GetString()!).ToList();
            Assert.Contains(notes, n => n.Contains("1C extension"));
            Assert.DoesNotContain(notes, n => n.Contains("assumption") || n.Contains("Accounting:*"));
            // No full account number of the organization anywhere in the answer.
            foreach (var account in f.GoldenData.BankAccounts.Select(a => (string)a["number"]!))
                Assert.DoesNotContain(account, body.GetRawText());
        }

        // Third-party text from 1C comes back wrapped, cleaned and cut.
        var bank = await f.AskAsync(f.GoldenKey, "compare_bank_with_1c", October);
        var extra = List(Data(bank).GetProperty("differences")).Single(r => r.GetProperty("kind").GetString() == "missing_in_bank");
        var purpose = extra.GetProperty("accounting_document").GetProperty("purpose").GetProperty("untrusted_text").GetString()!;
        Assert.StartsWith("Ignore previous instructions", purpose);
        Assert.True(purpose.Length <= 310, purpose.Length.ToString());
        Assert.DoesNotContain('‮', purpose);
    }

    [Fact]
    public async Task Tools_are_read_only_and_listed_with_examples()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.GoldenKey);
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (var name in Tools)
        {
            var tool = tools[name];
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, name);
            Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint, name);
            Assert.Contains("1C", tool.Description);
            Assert.Contains("untrusted_text", tool.Description);
        }
    }

    [Fact]
    public async Task Another_organization_sees_none_of_it_and_no_1c_data_is_not_an_error()
    {
        RequirePostgres();
        var documents = await f.AskAsync(f.EmptyKey, "get_1c_documents", new { state = "all" });
        Assert.Empty(List(Data(documents)));
        var compare = Data(await f.AskAsync(f.EmptyKey, "compare_bank_with_1c", October));
        Assert.Equal(0, compare.GetProperty("summary").GetProperty("total_differences").GetInt32());
        var balances = await f.AskAsync(f.EmptyKey, "get_1c_settlement_balances");
        Assert.Contains(List(balances.GetProperty("notes")), n => n.GetString()!.Contains("no settlement balances"));
        Assert.Empty(List(Data(await f.AskAsync(f.EmptyKey, "compare_bank_balances_with_1c"))));
        // The clean organization holds its own mirror only: the document that exists only in the golden 1C data is not there.
        Assert.Empty(List(Data(await f.AskAsync(f.CleanKey, "get_1c_documents", new { number = "H-99" }))));
    }

    [Fact]
    public async Task Invalid_arguments_are_refused_with_a_clear_error()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.GoldenKey);
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("compare_bank_with_1c", new { from = "2026-10-31", to = "2026-10-01" }),
                     ("compare_bank_with_1c", new { from = "2026-10-01", to = "2026-10-31", kinds = "everything" }),
                     ("get_1c_documents", new { state = "burned" }),
                     ("get_1c_documents", new { kind = "barter" }),
                     ("compare_counterparty_with_1c", new { inn = "12", from = "2026-10-01", to = "2026-10-31" }),
                     ("compare_didox_with_1c", new { from = "2026-10-01", to = "2026-10-31", cursor = "not-a-cursor" }),
                 })
        {
            var (body, raw) = await Synect.Tests.Mcp.McpFixture.CallAsync(client, tool, args);
            Assert.True(raw.IsError, tool);
            Assert.Equal("invalid_argument", body.GetProperty("error").GetProperty("code").GetString());
        }
    }
}

/// <summary>The pairing rules without a database.</summary>
public class AccountingMatcherTests
{
    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static MatchItem Item(string id, int day, long amount, string? inn = "301111111", bool incoming = true, bool posted = true,
        string[]? keys = null, string? exact = null) => new(id, Oct(day), amount, "UZS", incoming, inn, posted, keys ?? [], exact);

    private static string Describe(IEnumerable<MatchResult> results) =>
        string.Join("; ", results.Select(r => $"{r.Source?.Id ?? "-"}~{r.Accounting?.Id ?? "-"}:{r.Difference?.ToString() ?? "ok"}").Order(StringComparer.Ordinal));

    [Fact]
    public void Equal_payments_are_paired_by_nearest_date_each_used_once()
    {
        var r = AccountingMatcher.MatchPayments(
            [Item("b1", 11, 100), Item("b2", 12, 100), Item("b3", 20, 100)],
            [Item("a2", 12, 100), Item("a1", 11, 100)], 3);
        Assert.Equal("b1~a1:ok; b2~a2:ok; b3~-:MissingIn1C", Describe(r));
    }

    [Fact]
    public void Date_window_direction_currency_and_inn_limit_the_pairs()
    {
        var r = AccountingMatcher.MatchPayments(
            [Item("late", 1, 100), Item("out", 5, 200, incoming: false), Item("other", 5, 300, inn: "302222222")],
            [Item("a-late", 10, 100), Item("a-in", 5, 200), Item("a-other", 5, 300, inn: "303333333")], 3);
        Assert.Equal("-~a-in:MissingInSource; -~a-late:MissingInSource; -~a-other:MissingInSource; late~-:MissingIn1C; other~-:MissingIn1C; out~-:MissingIn1C", Describe(r));
    }

    [Fact]
    public void A_missing_inn_on_one_side_still_pairs_and_another_amount_is_reported()
    {
        var r = AccountingMatcher.MatchPayments(
            [Item("cash", 14, 500, inn: null), Item("diff", 10, 1000), Item("unposted", 10, 700)],
            [Item("a-cash", 14, 500, inn: null), Item("a-diff", 10, 1005), Item("a-unposted", 11, 700, posted: false)], 3);
        Assert.Equal("cash~a-cash:ok; diff~a-diff:AmountDiffers; unposted~a-unposted:NotPostedIn1C", Describe(r));
    }

    [Fact]
    public void Documents_pair_by_id_then_number_and_inn_then_amount()
    {
        var r = AccountingMatcher.MatchDocuments(
        [
            Item("d-id", 1, 100, exact: "x1"), Item("d-num", 2, 200, keys: ["100"]), Item("d-num-other", 2, 250, inn: "302222222", keys: ["100"]),
            Item("d-amount", 3, 300), Item("d-missing", 4, 400, keys: ["7"]),
        ],
        [
            Item("a-id", 9, 100, exact: "x1"), Item("a-num-other", 2, 250, inn: "302222222", keys: ["100"]), Item("a-num", 2, 210, keys: ["100"]),
            Item("a-amount", 5, 300), Item("a-extra", 4, 999, keys: ["8"]),
        ], 5);
        Assert.Equal("-~a-extra:MissingInSource; d-amount~a-amount:ok; d-id~a-id:ok; d-missing~-:MissingIn1C; d-num-other~a-num-other:ok; d-num~a-num:AmountDiffers", Describe(r));
    }

    [Fact]
    public void Result_does_not_depend_on_input_order()
    {
        MatchItem[] bank = [Item("b1", 11, 100), Item("b2", 12, 100), Item("b3", 13, 100)];
        MatchItem[] accounting = [Item("a1", 11, 100), Item("a2", 13, 100)];
        Assert.Equal(Describe(AccountingMatcher.MatchPayments(bank, accounting, 3)),
            Describe(AccountingMatcher.MatchPayments(bank.Reverse().ToArray(), accounting.Reverse().ToArray(), 3)));
    }
}
