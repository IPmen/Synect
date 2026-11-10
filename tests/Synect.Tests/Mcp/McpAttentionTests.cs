using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Access;
using Synect.Domain.Access;
using Synect.Tests.Postgres;
using static Synect.Application.Attention.AttentionRuleCodes;

namespace Synect.Tests.Mcp;

/// <summary>
/// Attention tools over the reconciliation golden set tenant (G) through the real MCP transport (PostgreSQL). The loads of
/// G evaluated the rules: a possible double payment (p10b) and a payment to another account (p11), both High.
/// </summary>
public class McpAttentionTests(McpFixture f) : IClassFixture<McpFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static List<JsonElement> Rows(JsonElement body) => body.GetProperty("data").EnumerateArray().ToList();

    private static IEnumerable<string> Notes(JsonElement body) => body.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!);

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    private async Task<string> ItemIdAsync(string rule)
    {
        await using var client = await f.ClientAsync(f.KeyG);
        var (body, _) = await McpFixture.CallAsync(client, "get_attention_items", new { status = "new,seen,snoozed,dismissed" });
        return Rows(body).Single(r => r.GetProperty("rule_code").GetString() == rule).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Summary_counts_by_severity_and_category_with_freshness_and_notes()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        var (body, raw) = await McpFixture.CallAsync(client, "get_attention_summary");
        Assert.True(raw.IsError != true, body.ToString());
        var data = body.GetProperty("data");
        Assert.Equal(2, data.GetProperty("open").GetInt32());
        Assert.Equal(2, data.GetProperty("open_by_severity").GetProperty("high").GetInt32());
        Assert.Equal(0, data.GetProperty("open_by_severity").GetProperty("critical").GetInt32());
        Assert.Equal(2, data.GetProperty("open_by_category").GetProperty("reconciliation").GetInt32());
        Assert.True(data.GetProperty("last_evaluated_at").ValueKind == JsonValueKind.String);
        Assert.Equal(2, body.GetProperty("data_freshness").GetProperty("sources").GetArrayLength());
        Assert.Contains(Notes(body), n => n.Contains("possible discrepancy") && n.Contains("Attention:*"));
        // Attention tools carry no attention block of their own.
        Assert.False(body.TryGetProperty("attention", out _));
        Assert.Equal(0, f.AdapterCalls.Calls);
    }

    [Fact]
    public async Task Items_are_sorted_filtered_and_paged()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        var (body, raw) = await McpFixture.CallAsync(client, "get_attention_items", new { status = "new,seen,snoozed" });
        Assert.True(raw.IsError != true, body.ToString());
        var rows = Rows(body);
        Assert.Equal([PaymentAccountMismatch, PossibleDuplicatePayment], rows.Select(r => r.GetProperty("rule_code").GetString()).Order());
        foreach (var r in rows)
        {
            Assert.Equal("high", r.GetProperty("severity").GetString());
            Assert.Equal("reconciliation", r.GetProperty("category").GetString());
            Assert.Equal(JsonValueKind.Object, r.GetProperty("details").ValueKind);
            Assert.Equal("transaction", r.GetProperty("entity").GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("title").GetString()));
        }
        // Account numbers in details are masked.
        var mismatch = rows.Single(r => r.GetProperty("rule_code").GetString() == PaymentAccountMismatch);
        Assert.Equal("****0099", mismatch.GetProperty("details").GetProperty("payment_account").GetString());
        Assert.DoesNotContain(Synect.Infrastructure.Adapters.Fake.ReconciliationGoldenSet.DOtherAccount, body.GetRawText());
        Assert.Equal(2, body.GetProperty("data_freshness").GetProperty("sources").GetArrayLength());

        foreach (var args in new object[] { new { severity = "critical,low" }, new { category = "documents" } })
        {
            var (filtered, _) = await McpFixture.CallAsync(client, "get_attention_items", args);
            Assert.True(Rows(filtered).Count == 0, filtered.GetRawText());
        }
        // The bank load alone (before the Didox documents) found possible double payments that the documents explained
        // (instalments of one act): they were resolved automatically and are listed only with status = resolved.
        var (resolved, _) = await McpFixture.CallAsync(client, "get_attention_items", new { status = "resolved" });
        Assert.All(Rows(resolved), r => Assert.Equal("resolved", r.GetProperty("status").GetString()));
        Assert.DoesNotContain(Rows(resolved), r => rows.Any(g => g.GetProperty("id").GetString() == r.GetProperty("id").GetString()));

        var (first, _) = await McpFixture.CallAsync(client, "get_attention_items", new { status = "new,seen,snoozed", limit = 1 });
        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        var (second, _) = await McpFixture.CallAsync(client, "get_attention_items", new { status = "new,seen,snoozed", limit = 1, cursor });
        Assert.NotEqual(Rows(first)[0].GetProperty("id").GetString(), Rows(second)[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);

        var (bad, badRaw) = await McpFixture.CallAsync(client, "get_attention_items", new { severity = "urgent" });
        Assert.True(badRaw.IsError);
        Assert.Equal("invalid_argument", Code(bad));

        // Another client sees nothing of G.
        await using var other = await f.ClientAsync(f.KeyB);
        var (b, _) = await McpFixture.CallAsync(other, "get_attention_items", new { status = "new,seen,snoozed,dismissed,resolved" });
        Assert.DoesNotContain(Rows(b), r => rows.Any(g => g.GetProperty("id").GetString() == r.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Update_needs_the_review_scope_follows_the_rules_and_is_journaled()
    {
        RequirePostgres();
        var id = await ItemIdAsync(PossibleDuplicatePayment);

        await using (var reader = await f.ClientAsync(f.KeyG))
        {
            var (denied, raw) = await McpFixture.CallAsync(reader, "update_attention_item", new { item_id = id, action = "seen" });
            Assert.True(raw.IsError);
            Assert.Equal("forbidden", Code(denied));
        }

        await using var client = await f.ClientAsync(f.KeyGReview);
        var (seen, seenRaw) = await McpFixture.CallAsync(client, "update_attention_item", new { item_id = id, action = "seen" });
        Assert.True(seenRaw.IsError != true, seen.ToString());
        Assert.Equal(("changed", "new", "seen"), (seen.GetProperty("data").GetProperty("result").GetString(),
            seen.GetProperty("data").GetProperty("previous_status").GetString(), seen.GetProperty("data").GetProperty("status").GetString()));
        Assert.Contains(Notes(seen), n => n.Contains("no source system"));

        var (snoozed, _) = await McpFixture.CallAsync(client, "update_attention_item", new { item_id = id, action = "snooze", days = 3 });
        Assert.Equal("snoozed", snoozed.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, snoozed.GetProperty("data").GetProperty("snoozed_until").ValueKind);

        foreach (var args in new object[]
                 {
                     new { item_id = id, action = "snooze", days = 3, until = "2026-12-01" },
                     new { item_id = id, action = "snooze" },
                     new { item_id = id, action = "snooze", days = 400 },
                     new { item_id = id, action = "dismiss" },
                     new { item_id = id, action = "archive" },
                     new { item_id = "not-a-uuid", action = "seen" },
                     new { item_id = Guid.NewGuid().ToString(), action = "seen" },
                 })
        {
            var (error, raw) = await McpFixture.CallAsync(client, "update_attention_item", args);
            Assert.True(raw.IsError, JsonSerializer.Serialize(args));
            Assert.Equal("invalid_argument", Code(error));
        }

        var (dismissed, _) = await McpFixture.CallAsync(client, "update_attention_item",
            new { item_id = id, action = "dismiss", comment = "Paid twice on purpose: advance for November" });
        Assert.Equal("dismissed", dismissed.GetProperty("data").GetProperty("status").GetString());
        var (reopened, _) = await McpFixture.CallAsync(client, "update_attention_item", new { item_id = id, action = "reopen" });
        Assert.Equal("new", reopened.GetProperty("data").GetProperty("status").GetString());

        // History with the API key, and the tool journal with what changed.
        var events = await f.QueryAdminAsync(
            $"SELECT kind, coalesce(api_key_id::text, '') FROM attention_item_events WHERE item_id = '{id}' ORDER BY at, id",
            r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(["Created", "Seen", "Snoozed", "Dismissed", "Reopened"], events.Select(e => e.Item1));
        Assert.All(events.Skip(1), e => Assert.Equal(f.KeyGReview.Id.ToString(), e.Item2));
        var audit = await f.QueryAdminAsync(
            $"SELECT status, coalesce(details::text, '') FROM tool_audit_log WHERE tenant_id = '{f.TenantG}' AND tool = 'update_attention_item' " +
            $"AND api_key_id = '{f.KeyGReview.Id}' ORDER BY started_at", r => (r.GetString(0), r.GetString(1)));
        Assert.Contains(audit, a => a.Item1 == "Ok" && a.Item2.Contains("\"previous_status\": \"New\"") && a.Item2.Contains("\"new_status\": \"Seen\""));
        Assert.Contains(audit, a => a.Item1 == "Ok" && a.Item2.Contains("\"new_status\": \"Dismissed\""));
        Assert.Contains(audit, a => a.Item1 == "Error");
        var forbidden = await f.QueryAdminAsync(
            $"SELECT count(*) FROM tool_audit_log WHERE tenant_id = '{f.TenantG}' AND tool = 'update_attention_item' AND api_key_id = '{f.KeyG.Id}'",
            r => r.GetInt64(0));
        Assert.Equal(1L, forbidden.Single());
    }

    [Fact]
    public async Task Other_tools_carry_the_attention_block_and_remind_at_most_once_per_interval_per_key()
    {
        RequirePostgres();
        var api = f.CreateApi(("Attention:ReminderInterval", "00:00:02"));
        var key = await f.Host.InTenantAsync(f.TenantG, sp => sp.GetRequiredService<ApiKeyService>()
            .CreateAsync("G reminder", [ApiKey.ScopeRead], CancellationToken.None));
        f.AllKeys.Add(key.Key);
        await using var client = await f.ClientAsync(key, api);

        static bool Reminded(JsonElement body) => Notes(body).Any(n => n.Contains("new items that need attention; call get_attention_items"));

        var (first, _) = await McpFixture.CallAsync(client, "get_data_freshness");
        var block = first.GetProperty("attention");
        var count = block.GetProperty("new_items").GetInt32();
        Assert.True(count > 0);
        Assert.Equal("get_attention_items", block.GetProperty("tool").GetString());
        Assert.True(block.TryGetProperty("new_critical", out _));
        Assert.Contains($"There are {count} new items that need attention; call get_attention_items", Notes(first));

        var (second, _) = await McpFixture.CallAsync(client, "get_unpaid_documents", new { from = "2026-10-01", to = "2026-10-31" });
        Assert.Equal(count, second.GetProperty("attention").GetProperty("new_items").GetInt32());
        Assert.False(Reminded(second));
        var (summary, _) = await McpFixture.CallAsync(client, "get_attention_summary");
        Assert.False(summary.TryGetProperty("attention", out _));
        Assert.False(Reminded(summary));

        // Another key of the same client gets its own reminder.
        await using (var other = await f.ClientAsync(f.KeyGReview, api))
            Assert.True(Reminded((await McpFixture.CallAsync(other, "get_data_freshness")).Body));

        // After the interval the same key is reminded again.
        await Task.Delay(TimeSpan.FromMilliseconds(2200), TestContext.Current.CancellationToken);
        Assert.True(Reminded((await McpFixture.CallAsync(client, "get_data_freshness")).Body));

        // Error answers have no block; a client without new items gets the block with zero and no reminder.
        var (error, _) = await McpFixture.CallAsync(client, "get_cash_flow", new { from = "x", to = "y" });
        Assert.False(error.TryGetProperty("attention", out _));
        await using var c = await f.ClientAsync(f.KeyC, api);
        var (quiet, _) = await McpFixture.CallAsync(c, "get_data_freshness");
        Assert.Equal(0, quiet.GetProperty("attention").GetProperty("new_items").GetInt32());
        Assert.False(Reminded(quiet));
    }

    [Fact]
    public async Task Attention_tools_are_listed_with_examples_and_the_right_annotations()
    {
        RequirePostgres();
        await using var client = await f.ClientAsync(f.KeyG);
        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);
        foreach (var name in new[] { "get_attention_items", "get_attention_summary", "update_attention_item" })
        {
            var d = tools[name].Description;
            Assert.Contains("«", d);
            Assert.Matches("[ʻ'ʼ]|ҳ|ў|қ|ғ", d); // Uzbek examples (Latin or Cyrillic)
            Assert.Contains("untrusted_text", d);
        }
        Assert.Contains("possible discrepancy", tools["get_attention_items"].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start of a conversation", tools["get_attention_summary"].Description);
        Assert.Equal(false, tools["update_attention_item"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal(ApiKey.ScopeReview, Synect.Mcp.Hosting.ToolCallFilter.RequiredScope("update_attention_item"));
    }
}
