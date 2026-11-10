using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Synect.Domain.Access;
using Synect.Domain.Reconciliation;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;
using static Synect.Infrastructure.Adapters.Fake.ReconciliationGoldenSet;

namespace Synect.Tests.Mcp;

/// <summary>Reconciliation tools over the golden set tenant (G) through the real MCP transport (PostgreSQL).</summary>
public class McpReconciliationTests(McpFixture f) : IClassFixture<McpFixture>
{
    private static readonly object October = new { from = "2026-10-01", to = "2026-10-31" };

    public static readonly string[] ReconciliationTools =
    [
        "get_paid_documents", "get_unpaid_documents", "get_partially_paid_documents", "get_unmatched_payments", "get_signed_unpaid_acts",
        "get_multi_document_payments", "get_amount_differences", "get_payments_without_reference", "get_payment_delay_stats",
        "get_account_mismatches", "get_possible_duplicate_payments", "get_counterparty_balance_estimate",
    ];

    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static List<JsonElement> Rows(JsonElement body) => body.GetProperty("data").EnumerateArray().ToList();

    private static IEnumerable<string> Notes(JsonElement body) => body.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!);

    [Fact]
    public async Task Every_reconciliation_tool_answers_with_freshness_notes_and_explicit_directions()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        foreach (var tool in ReconciliationTools)
        {
            var args = tool == "get_counterparty_balance_estimate" ? (object)new { as_of = "2026-10-31" } : October;
            var (body, raw) = await McpFixture.CallAsync(client, tool, args);
            Assert.True(raw.IsError != true, $"{tool}: {body}");
            Assert.Equal(2, body.GetProperty("data_freshness").GetProperty("sources").GetArrayLength());
            Assert.Contains(Notes(body), n => n.Contains("possible matches with a confidence score") && n.Contains("the accountant makes the decision"));
            Assert.True(body.GetProperty("returned_rows").GetInt32() > 0, $"{tool} returned no rows on the golden set: {body}");
        }
        Assert.Equal(0, f.AdapterCalls.Calls);
    }

    [Fact]
    public async Task Paid_unpaid_and_partially_paid_documents()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);

        var (paid, _) = await McpFixture.CallAsync(client, "get_paid_documents", October);
        var d1 = Rows(paid).Single(d => d.GetProperty("number").GetString() == "СФ-101");
        Assert.Equal("outgoing", d1.GetProperty("document_direction").GetString());
        Assert.Equal("incoming", d1.GetProperty("expected_payment_direction").GetString());
        Assert.Equal(1_200_000_00, d1.GetProperty("paid").GetProperty("amount_minor").GetInt64());
        Assert.Equal("2026-10-05", d1.GetProperty("last_payment_date").GetString());
        var match = d1.GetProperty("matches").EnumerateArray().Single();
        Assert.Equal("exact", match.GetProperty("type").GetString());
        Assert.Equal("proposed", match.GetProperty("status").GetString());
        Assert.Equal(100, match.GetProperty("confidence").GetInt32());
        Assert.Contains(match.GetProperty("reasons").EnumerateArray(), r => r.GetProperty("code").GetString() == "DocumentNumberInPurpose");

        var (partial, _) = await McpFixture.CallAsync(client, "get_partially_paid_documents", October);
        var d6 = Rows(partial).Single(d => d.GetProperty("number").GetString() == "СФ-103");
        Assert.Equal(1_000_000_00, d6.GetProperty("remaining").GetProperty("amount_minor").GetInt64());

        var (unpaid, _) = await McpFixture.CallAsync(client, "get_unpaid_documents", new { from = "2026-10-01", to = "2026-10-31", inn = H });
        var ambiguous = Rows(unpaid).Where(d => d.GetProperty("number").GetString() is "H-1" or "H-2").ToList();
        Assert.Equal(2, ambiguous.Count);
        Assert.Contains(ambiguous, d => d.GetProperty("nearest_candidate").ValueKind == JsonValueKind.Object);
        Assert.Contains(Notes(unpaid), n => n.Contains("nearest_candidate is a hint"));
    }

    [Fact]
    public async Task Unmatched_payments_show_without_inn_currency_and_hints()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        var (body, _) = await McpFixture.CallAsync(client, "get_unmatched_payments", October);
        var rows = Rows(body);
        var noInn = rows.Single(r => r.GetProperty("without_inn").GetBoolean());
        Assert.Equal("without_inn", noInn.GetProperty("excluded_reason").GetString());
        Assert.Contains(rows, r => r.GetProperty("excluded_reason").GetString() == "currency_not_reconciled"
                                   && r.GetProperty("amount").GetProperty("currency").GetString() == "USD");
        Assert.All(rows, r => Assert.StartsWith("****", r.GetProperty("account_number").GetString()));
        Assert.All(rows, r => Assert.True(r.GetProperty("purpose").TryGetProperty("untrusted_text", out _)));

        // G5: expense payments without a counterparty document.
        var (outgoing, _) = await McpFixture.CallAsync(client, "get_unmatched_payments", new { from = "2026-10-01", to = "2026-10-31", direction = "outgoing", without_inn = "exclude" });
        var refund = Assert.Single(Rows(outgoing));
        Assert.Equal(G, refund.GetProperty("inn").GetString());
        Assert.False(refund.GetProperty("counterparty_has_documents").GetBoolean()); // G has no incoming documents
    }

    [Fact]
    public async Task Multi_document_payments_differences_mismatches_duplicates_and_delays()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);

        var (multi, _) = await McpFixture.CallAsync(client, "get_multi_document_payments", October);
        Assert.Equal(["many_to_one", "one_to_many", "one_to_many"], Rows(multi).Select(m => m.GetProperty("type").GetString()).Order());

        var (diff, _) = await McpFixture.CallAsync(client, "get_amount_differences", October);
        var differences = Rows(diff).Select(m => m.GetProperty("difference").GetProperty("amount_minor").GetInt64()).Order().ToList();
        Assert.Equal([-1_000_000_00L, 20_000_00L], differences); // partial СФ-103 and overpaid 47

        var (mismatch, _) = await McpFixture.CallAsync(client, "get_account_mismatches", October);
        var m = Assert.Single(Rows(mismatch));
        Assert.Equal(D, m.GetProperty("inn").GetString());
        Assert.Equal("****0099", m.GetProperty("payment_account").GetString());
        Assert.Equal("****0001", m.GetProperty("seller_account").GetString());

        var (dups, _) = await McpFixture.CallAsync(client, "get_possible_duplicate_payments", October);
        var dup = Assert.Single(Rows(dups));
        Assert.Equal(90, dup.GetProperty("confidence").GetInt32());
        Assert.False(dup.GetProperty("payment_matched").GetBoolean());
        Assert.True(dup.GetProperty("earlier_payment_matched").GetBoolean());

        var (delays, _) = await McpFixture.CallAsync(client, "get_payment_delay_stats", new { from = "2026-10-01", to = "2026-10-31", inn = A });
        var a = Assert.Single(delays.GetProperty("data").GetProperty("counterparties").EnumerateArray());
        Assert.Equal("outgoing", a.GetProperty("document_direction").GetString());
        Assert.Contains(Notes(delays), n => n.Contains("DOCUMENT date") && n.Contains("signing date"));
        // Overall figures for one counterparty equal its own.
        var single = Assert.Single(delays.GetProperty("data").GetProperty("overall").EnumerateArray());
        Assert.Equal(1, single.GetProperty("counterparties").GetInt32());
        Assert.Equal(a.GetProperty("average_days").GetDecimal(), single.GetProperty("average_days").GetDecimal());
        Assert.Equal(a.GetProperty("average_days").GetDecimal(), single.GetProperty("average_of_counterparties").GetDecimal());

        // E11 over all clients: one row per counterparty plus the overall average, consistent with the rows.
        var (all, _) = await McpFixture.CallAsync(client, "get_payment_delay_stats", new { from = "2026-10-01", to = "2026-10-31", direction = "outgoing" });
        var rows = all.GetProperty("data").GetProperty("counterparties").EnumerateArray().ToList();
        Assert.True(rows.Count >= 2, all.GetRawText());
        Assert.All(rows, r => Assert.Equal("outgoing", r.GetProperty("document_direction").GetString()));
        var overall = Assert.Single(all.GetProperty("data").GetProperty("overall").EnumerateArray());
        Assert.Equal(rows.Count, overall.GetProperty("counterparties").GetInt32());
        var links = rows.Sum(r => r.GetProperty("payment_document_links").GetInt32());
        Assert.Equal(links, overall.GetProperty("payment_document_links").GetInt32());
        Assert.Equal(rows.Min(r => r.GetProperty("min_days").GetInt32()), overall.GetProperty("min_days").GetInt32());
        Assert.Equal(rows.Max(r => r.GetProperty("max_days").GetInt32()), overall.GetProperty("max_days").GetInt32());
        var meanOfMeans = rows.Average(r => r.GetProperty("average_days").GetDecimal());
        Assert.InRange(overall.GetProperty("average_of_counterparties").GetDecimal(), meanOfMeans - 0.1m, meanOfMeans + 0.1m);

        var (refs, _) = await McpFixture.CallAsync(client, "get_payments_without_reference", October);
        Assert.Contains(Rows(refs), r => r.GetProperty("purpose").GetProperty("untrusted_text").GetString() == "Оплата за услуги");
    }

    [Fact]
    public async Task Balance_estimate_is_documents_minus_matched_payments()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        var (body, _) = await McpFixture.CallAsync(client, "get_counterparty_balance_estimate", new { inn = C, as_of = "2026-10-31" });
        var c = Assert.Single(Rows(body));
        // C documents: 45 (750 000), 46 (333 333.33), 47 (100 000); payments matched: 750 000 + 333 333.33 + 100 000 (overpayment allocated to the document).
        Assert.Equal(1_183_333_33, c.GetProperty("outgoing_documents").GetProperty("amount_minor").GetInt64());
        Assert.Equal(0, c.GetProperty("receivable_estimate").GetProperty("amount_minor").GetInt64());
        Assert.Equal(20_000_00, c.GetProperty("unmatched_incoming_payments").GetProperty("amount_minor").GetInt64()); // the overpaid part
        Assert.Contains(Notes(body), n => n.Contains("ESTIMATE") && n.Contains("не бухгалтерский долг"));

        var (all, _) = await McpFixture.CallAsync(client, "get_counterparty_balance_estimate", new { as_of = "2026-10-31" });
        Assert.Contains(Rows(all), r => r.GetProperty("inn").GetString() == H
                                        && r.GetProperty("receivable_estimate").GetProperty("amount_minor").GetInt64() == 2_109_000_00);
    }

    [Fact]
    public async Task Match_details_and_review_scope()
    {
        RequirePostgres();
        var matchId = await f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatches
            .Where(m => m.CounterpartyInn == E && m.Status == MatchStatus.Proposed).Select(m => m.Id).SingleAsync());

        await using (var reader = await f.ClientAsync(f.KeyG))
        {
            var (details, _) = await McpFixture.CallAsync(reader, "get_match_details", new { match_id = matchId.ToString() });
            var match = details.GetProperty("data").GetProperty("match");
            Assert.Equal(E, match.GetProperty("inn").GetString());
            Assert.Single(match.GetProperty("payments").EnumerateArray());
            Assert.Equal("100", match.GetProperty("documents")[0].GetProperty("number").GetString());
            Assert.StartsWith("r1-", details.GetProperty("data").GetProperty("rules_version").GetString());

            // Without the review scope: forbidden, nothing changes.
            var (denied, raw) = await McpFixture.CallAsync(reader, "confirm_match", new { match_id = matchId.ToString() });
            Assert.True(raw.IsError);
            Assert.Equal("forbidden", denied.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal(MatchStatus.Proposed, await StatusAsync(matchId));

        await using (var reviewer = await f.ClientAsync(f.KeyGReview))
        {
            var (ok, raw) = await McpFixture.CallAsync(reviewer, "confirm_match", new { match_id = matchId.ToString(), comment = "Checked against the contract" });
            Assert.True(raw.IsError != true, ok.GetRawText());
            Assert.Equal("confirmed", ok.GetProperty("data").GetProperty("status").GetString());
            Assert.Contains(Notes(ok), n => n.Contains("no source system"));
            var (again, _) = await McpFixture.CallAsync(reviewer, "confirm_match", new { match_id = matchId.ToString() });
            Assert.Equal("unchanged", again.GetProperty("data").GetProperty("result").GetString());

            // A decision is not final: the accountant can change it both ways.
            var (rejected, rejectedRaw) = await McpFixture.CallAsync(reviewer, "reject_match", new { match_id = matchId.ToString(), comment = "Wrong invoice" });
            Assert.True(rejectedRaw.IsError != true, rejected.GetRawText());
            Assert.Equal(("rejected", "confirmed"), (rejected.GetProperty("data").GetProperty("status").GetString(),
                rejected.GetProperty("data").GetProperty("previous_status").GetString()));
            Assert.Contains(Notes(rejected), n => n.Contains("decision history"));
            Assert.Equal(MatchStatus.Rejected, await StatusAsync(matchId));

            var (back, backRaw) = await McpFixture.CallAsync(reviewer, "confirm_match", new { match_id = matchId.ToString(), comment = "Checked against the contract" });
            Assert.True(backRaw.IsError != true, back.GetRawText());
            Assert.Equal("rejected", back.GetProperty("data").GetProperty("previous_status").GetString());
        }

        var stored = await f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatches.AsNoTracking().SingleAsync(m => m.Id == matchId));
        Assert.Equal((MatchStatus.Confirmed, f.KeyGReview.Id, "Checked against the contract"), (stored.Status, stored.DecidedByKey!.Value, stored.DecisionComment));
        var items = await f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatchItems.AsNoTracking().Where(i => i.MatchId == matchId).ToListAsync());
        Assert.All(items, i => Assert.Equal(MatchStatus.Confirmed, i.MatchStatus));

        // Decision history: who, when, previous and new status, comment.
        var history = await f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatchDecisions.AsNoTracking()
            .Where(d => d.MatchId == matchId).OrderBy(d => d.DecidedAt).ThenBy(d => d.Id).ToListAsync());
        Assert.Equal(
            [(MatchStatus.Proposed, MatchStatus.Confirmed, "Checked against the contract"),
             (MatchStatus.Confirmed, MatchStatus.Rejected, "Wrong invoice"),
             (MatchStatus.Rejected, MatchStatus.Confirmed, "Checked against the contract")],
            history.Select(h => (h.PreviousStatus, h.NewStatus, h.Comment!)));
        Assert.All(history, h => Assert.Equal(f.KeyGReview.Id, h.DecidedByKey));

        var audit = await f.QueryAdminAsync(
            $"SELECT tool, status, parameters::text, coalesce(details::text, '') FROM tool_audit_log WHERE tenant_id = '{f.TenantG}' " +
            "AND tool IN ('confirm_match', 'reject_match') AND api_key_id IN " +
            $"('{f.KeyG.Id}', '{f.KeyGReview.Id}') AND parameters->>'match_id' = '{matchId}' ORDER BY started_at",
            r => (Tool: r.GetString(0), Status: r.GetString(1), Parameters: r.GetString(2), Details: r.GetString(3)));
        Assert.Equal(["confirm_match", "confirm_match", "confirm_match", "reject_match", "confirm_match"], audit.Select(a => a.Tool));
        Assert.Equal(["Error", "Ok", "Ok", "Ok", "Ok"], audit.Select(a => a.Status));
        Assert.Contains("Checked against the contract", audit[1].Parameters);
        Assert.Contains(matchId.ToString(), audit[1].Parameters);
        Assert.Equal("", audit[0].Details); // forbidden: nothing done
        Assert.Contains("\"previous_status\": \"Proposed\"", audit[1].Details);
        Assert.Contains("\"changed\": false", audit[2].Details);
        Assert.Contains("\"previous_status\": \"Confirmed\"", audit[3].Details);
        Assert.Contains("\"new_status\": \"Rejected\"", audit[3].Details);
        Assert.Contains("\"previous_status\": \"Rejected\"", audit[4].Details);
    }

    private Task<MatchStatus> StatusAsync(Guid id) =>
        f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatches.Where(m => m.Id == id).Select(m => m.Status).SingleAsync());

    [Fact]
    public async Task Tenants_never_see_each_others_reconciliation()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyA);
        var (paid, _) = await McpFixture.CallAsync(client, "get_paid_documents", October);
        Assert.Empty(Rows(paid));
        var foreign = await f.Host.QueryAsync(f.TenantG, db => db.ReconciliationMatches.Select(m => m.Id).FirstAsync());
        var (details, _) = await McpFixture.CallAsync(client, "get_match_details", new { match_id = foreign.ToString() });
        Assert.Equal(JsonValueKind.Null, details.GetProperty("data").ValueKind);
        await using var reviewer = await f.ClientAsync(f.KeyGReview);
        var (denied, _) = await McpFixture.CallAsync(reviewer, "reject_match", new { match_id = Guid.NewGuid().ToString() });
        Assert.Equal("invalid_argument", denied.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Non_reconciliation_tools_say_they_only_check_documents_by_inn()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyA);
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("get_payments_without_documents", October),
                     ("get_counterparty_summary", new { inn = Synect.Tests.Analytics.AnalyticsFixture.Alpha, from = "2026-09-01", to = "2026-10-31" }),
                 })
        {
            var (body, _) = await McpFixture.CallAsync(client, tool, args);
            Assert.Contains(Notes(body), n => n.Contains("not a matching of payments with documents") && n.Contains("а не сопоставление платежей с документами"));
        }
        var tools = await client.ListToolsAsync();
        Assert.Contains("does NOT match payments with documents", tools.Single(t => t.Name == "get_payments_without_documents").Description);
    }
}

/// <summary>HTTP limits of /mcp: request ceiling per IP and per key, failed authentications per IP (429 with Retry-After).</summary>
public class McpHttpLimitTests(McpFixture f) : IClassFixture<McpFixture>
{
    private static StringContent ListTools() =>
        new("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = ListTools() };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    [Fact]
    public async Task Requests_over_the_ip_ceiling_get_429_with_retry_after()
    {
        Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);
        var api = f.CreateApi(("Mcp:HttpRateLimit:RequestsPerMinutePerIp", "3"));
        var http = api.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(f.KeyA.Key))).StatusCode);
        var limited = await http.SendAsync(Post(f.KeyA.Key));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
        Assert.Contains("rate_limited", await limited.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await f.RawClient().SendAsync(Post(f.KeyA.Key))).StatusCode); // other host, own limits
    }

    [Fact]
    public async Task Requests_over_the_key_ceiling_get_429()
    {
        Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);
        var api = f.CreateApi(("Mcp:HttpRateLimit:RequestsPerMinutePerKey", "2"));
        var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(f.KeyB.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(f.KeyB.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.SendAsync(Post(f.KeyB.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(f.KeyC.Key))).StatusCode); // another key is not affected
    }

    [Fact]
    public async Task Failed_authentications_are_limited_per_ip()
    {
        Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);
        var api = f.CreateApi(("Mcp:HttpRateLimit:FailedAuthPerMinutePerIp", "2"));
        var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post("sk_synect_wrongwrongwrongwrongwrongwrongwrongwrong1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(null))).StatusCode);
        var blocked = await http.SendAsync(Post(f.KeyA.Key)); // even a valid key from this IP waits for the window
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
        Assert.Contains("too_many_failed_authentications", await blocked.Content.ReadAsStringAsync());
        Assert.DoesNotContain(f.KeyA.Key[ApiKeyFormatPrefixLength..], string.Join("\n", f.ApiLogs.Lines));
    }

    private const int ApiKeyFormatPrefixLength = Synect.Application.Access.ApiKeyFormat.StoredPrefixLength;
}
