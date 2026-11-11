using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;
using Synect.Mcp.Hosting;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Mcp;

/// <summary>Accounts and organizations through the MCP endpoint (stage 6.5).</summary>
public class McpAccountTests(McpAccountFixture f) : IClassFixture<McpAccountFixture>
{
    private void Require() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static string? Code(JsonElement body) => body.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    private static string Name(JsonElement organization) => organization.GetProperty("name").GetProperty("untrusted_text").GetString()!;

    private static List<string> ListedTins(JsonElement body) =>
        body.GetProperty("organizations").GetProperty("listed").EnumerateArray().Select(o => o.GetProperty("tin").GetString()!).Order().ToList();

    [Fact]
    public async Task List_organizations_shows_only_the_organizations_of_the_key()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "list_organizations");
        Assert.True(raw.IsError != true, body.GetRawText());
        Assert.Equal("several", body.GetProperty("organization").GetProperty("scope").GetString());
        var orgs = body.GetProperty("data").GetProperty("organizations").EnumerateArray().ToList();
        Assert.Equal(["301111111", "302222222", "303333333"], orgs.Select(o => o.GetProperty("organization").GetProperty("tin").GetString()!).Order().ToList());
        var south = orgs.Single(o => o.GetProperty("organization").GetProperty("tin").GetString() == "303333333");
        Assert.True(south.GetProperty("new_attention").GetProperty("critical").GetInt32() >= 1);
        Assert.Equal(1, south.GetProperty("connections").GetProperty("total").GetInt32());

        await using var b = await f.ClientAsync(f.KeyB);
        var (bBody, _) = await f.CallAsync(b, "list_organizations");
        var bOrgs = bBody.GetProperty("data").GetProperty("organizations").EnumerateArray().ToList();
        Assert.Equal("304444444", Assert.Single(bOrgs).GetProperty("organization").GetProperty("tin").GetString());
    }

    [Fact]
    public async Task A_key_with_several_organizations_must_name_one_and_gets_the_list()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "get_account_balances");
        Assert.True(raw.IsError);
        Assert.Equal("organization_required", Code(body));
        Assert.Contains("never choose", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["301111111", "302222222", "303333333"], ListedTins(body));
        Assert.Equal(3, body.GetProperty("organizations").GetProperty("total").GetInt32());

        // The answer is journaled in account_tool_audit_log (no single organization was resolved).
        var rows = await f.QueryAdminAsync($"SELECT error_message FROM account_tool_audit_log WHERE api_key_id = '{f.KeyA.Id}' AND tool = 'get_account_balances'",
            r => r.GetString(0));
        Assert.Contains(rows, m => m.StartsWith("organization_required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("301111111")]
    [InlineData("O'zbek Qurilish")]
    [InlineData("Ўзбек Қурилиш МЧЖ")]
    [InlineData("Uzbek Kurilish")]
    public async Task Organization_by_tin_or_any_spelling_of_the_name(string value)
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "get_account_balances", new { organization = value });
        Assert.True(raw.IsError != true, body.GetRawText());
        var organization = body.GetProperty("organization");
        Assert.Equal("301111111", organization.GetProperty("tin").GetString());
        Assert.Equal("ООО «Ўзбек Қурилиш»", Name(organization));
        Assert.Equal(f.Uzbek.TenantId, organization.GetProperty("organization_id").GetGuid());
        // The data is the organization's own: its connection only.
        var sources = body.GetProperty("data_freshness").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("connection_id").GetGuid()).ToList();
        Assert.Equal([f.BankOf[f.Uzbek.TenantId]], sources);
    }

    [Fact]
    public async Task Similar_names_are_ambiguous()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "get_cash_flow", new { organization = "Ромашка", from = "2026-11-01", to = "2026-11-06" });
        Assert.True(raw.IsError);
        Assert.Equal("organization_ambiguous", Code(body));
        Assert.Equal(["302222222", "303333333"], ListedTins(body));
    }

    [Fact]
    public async Task Organizations_of_another_account_do_not_exist_for_the_key()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var answers = new List<(string Value, JsonElement Body)>();
        foreach (var value in new[] { f.Beta.Tin!, "Бета Трейд", f.Beta.TenantId.ToString(), "399999999", "Несуществующая фирма", Guid.NewGuid().ToString() })
        {
            var (body, raw) = await f.CallAsync(a, "get_account_balances", new { organization = value });
            Assert.True(raw.IsError);
            answers.Add((value, body));
        }
        // Exactly the same answer (code, message apart from the echoed value, listed organizations) for an organization of
        // another account and for one that does not exist.
        var first = answers[0];
        foreach (var (value, body) in answers)
        {
            Assert.Equal("organization_not_found", Code(body));
            Assert.Equal(first.Body.GetProperty("error").GetProperty("message").GetString()!.Replace(first.Value, "<v>"),
                body.GetProperty("error").GetProperty("message").GetString()!.Replace(value, "<v>"));
            Assert.Equal(ListedTins(first.Body), ListedTins(body));
            Assert.DoesNotContain("304444444", body.GetRawText().Replace(value, ""));
            Assert.DoesNotContain("Бета", body.GetRawText().Replace(value, ""));
        }
    }

    [Fact]
    public async Task A_key_limited_to_one_organization_needs_no_parameter_and_sees_no_other()
    {
        Require();
        await using var north = await f.ClientAsync(f.KeyANorth);
        var (body, raw) = await f.CallAsync(north, "get_account_balances");
        Assert.True(raw.IsError != true, body.GetRawText());
        Assert.Equal("302222222", body.GetProperty("organization").GetProperty("tin").GetString());

        var (other, otherRaw) = await f.CallAsync(north, "get_account_balances", new { organization = f.Uzbek.Tin });
        Assert.True(otherRaw.IsError);
        Assert.Equal("organization_not_found", Code(other));
        Assert.Equal(["302222222"], ListedTins(other));
    }

    [Fact]
    public async Task Single_organization_keys_work_as_before_and_name_their_organization()
    {
        Require();
        await using var solo = await f.ClientAsync(f.KeySolo);
        var (body, raw) = await f.CallAsync(solo, "get_data_freshness");
        Assert.True(raw.IsError != true, body.GetRawText());
        Assert.Equal("Solo client", Name(body.GetProperty("organization")));
        Assert.True(body.TryGetProperty("attention", out _));
        var audit = await f.QueryAdminAsync($"SELECT tenant_id, account_id IS NULL FROM tool_audit_log WHERE api_key_id = '{f.KeySolo.Id}'",
            r => (r.GetGuid(0), r.GetBoolean(1)));
        Assert.Contains((f.Solo, true), audit);
    }

    [Fact]
    public async Task Revoked_keys_and_disabled_organizations_do_not_work()
    {
        Require();
        var http = f.Api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.KeyRevoked.Key);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // A fourth organization of A, disabled: invisible to A's keys afterwards.
        var admin = f.Admin();
        await using (admin.Db)
        {
            var extra = (await admin.AddOrganizationAsync(f.AccountA, "Временная фирма", "305555555", createTenant: true)).Organization;
            await using var a = await f.ClientAsync(f.KeyA);
            var (ok, okRaw) = await f.CallAsync(a, "get_data_freshness", new { organization = "305555555" });
            Assert.True(okRaw.IsError != true, ok.GetRawText());
            await admin.DisableOrganizationAsync(f.AccountA, extra.Tin!);
            var (gone, goneRaw) = await f.CallAsync(a, "get_data_freshness", new { organization = "305555555" });
            Assert.True(goneRaw.IsError);
            Assert.Equal("organization_not_found", Code(gone));
            var (list, _) = await f.CallAsync(a, "list_organizations");
            Assert.DoesNotContain("305555555", list.GetRawText());
        }
    }

    [Fact]
    public async Task Audit_has_the_account_and_the_organization_of_each_call()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        await f.CallAsync(a, "get_account_balances", new { organization = f.North.Tin });
        var single = await f.QueryAdminAsync(
            $"SELECT tenant_id, account_id FROM tool_audit_log WHERE api_key_id = '{f.KeyA.Id}' AND tool = 'get_account_balances'",
            r => (r.GetGuid(0), r.GetGuid(1)));
        Assert.Contains((f.North.TenantId, f.AccountA), single);

        await f.CallAsync(a, "get_portfolio_overview");
        var perOrganization = await f.QueryAdminAsync(
            $"SELECT tenant_id, details->>'multi_organization' FROM tool_audit_log WHERE api_key_id = '{f.KeyA.Id}' AND tool = 'get_portfolio_overview'",
            r => (r.GetGuid(0), r.GetString(1)));
        foreach (var tenant in new[] { f.Uzbek.TenantId, f.North.TenantId, f.South.TenantId })
            Assert.Contains((tenant, "true"), perOrganization);
        Assert.DoesNotContain(perOrganization, r => r.Item1 == f.Beta.TenantId);
        var accountRows = await f.QueryAdminAsync(
            $"SELECT cardinality(tenant_ids) FROM account_tool_audit_log WHERE api_key_id = '{f.KeyA.Id}' AND tool = 'get_portfolio_overview'",
            r => r.GetInt32(0));
        Assert.Contains(3, accountRows);
    }

    [Fact]
    public async Task Calls_over_the_per_account_concurrency_limit_are_rejected()
    {
        Require();
        var api = f.CreateApi(("Mcp:RateLimit:MaxConcurrentPerAccount", "1"));
        await using var a = await f.ClientAsync(f.KeyA, api);
        var gate = api.Services.GetRequiredService<ToolCallGate>();
        // A call of another key of the same account is running (on another organization).
        var running = gate.TryEnter(f.KeyANorth.Id, f.North.TenantId, f.AccountA, out _);
        Assert.NotNull(running);
        var (body, raw) = await f.CallAsync(a, "get_data_freshness", new { organization = f.Uzbek.Tin });
        Assert.True(raw.IsError);
        Assert.Equal("rate_limited", Code(body));
        Assert.Contains("per account", body.GetProperty("error").GetProperty("message").GetString());

        // Keys of another account are not affected.
        await using (var b = await f.ClientAsync(f.KeyB, api))
        {
            var (other, otherRaw) = await f.CallAsync(b, "get_data_freshness");
            Assert.True(otherRaw.IsError != true, other.GetRawText());
        }
        running!.Dispose();
        var (ok, okRaw) = await f.CallAsync(a, "get_data_freshness", new { organization = f.Uzbek.Tin });
        Assert.True(okRaw.IsError != true, ok.GetRawText());
    }

    [Fact]
    public async Task Portfolio_overview_is_sorted_by_importance_and_paged()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "get_portfolio_overview", new { limit = 2 });
        Assert.True(raw.IsError != true, body.GetRawText());
        var page1 = body.GetProperty("data").GetProperty("organizations").EnumerateArray().ToList();
        Assert.Equal(2, page1.Count);
        Assert.Equal(3, body.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal("303333333", page1[0].GetProperty("organization").GetProperty("tin").GetString()); // critical first
        Assert.Equal("critical", page1[0].GetProperty("top_severity").GetString());
        Assert.Equal("301111111", page1[1].GetProperty("organization").GetProperty("tin").GetString()); // then high
        foreach (var row in page1)
        {
            Assert.True(row.GetProperty("sources").GetArrayLength() >= 1);
            Assert.True(row.GetProperty("unmatched_payments").GetProperty("count").GetInt32() >= 0);
            Assert.True(row.GetProperty("documents_awaiting_our_signature").GetInt32() >= 0);
            Assert.True(row.GetProperty("new_attention").GetProperty("total").GetInt32() >= 1);
        }
        var cursor = body.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        var (next, _) = await f.CallAsync(a, "get_portfolio_overview", new { limit = 2, cursor });
        var page2 = next.GetProperty("data").GetProperty("organizations").EnumerateArray().ToList();
        Assert.Equal("302222222", Assert.Single(page2).GetProperty("organization").GetProperty("tin").GetString());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("next_cursor").ValueKind);

        // A list of organizations (TIN and name), never one of another account.
        var (some, _) = await f.CallAsync(a, "get_portfolio_overview", new { organizations = "302222222, O'zbek Qurilish" });
        Assert.Equal(["301111111", "302222222"], some.GetProperty("data").GetProperty("organizations").EnumerateArray()
            .Select(o => o.GetProperty("organization").GetProperty("tin").GetString()!).Order().ToList());
        var (foreign, foreignRaw) = await f.CallAsync(a, "get_portfolio_overview", new { organizations = f.Beta.Tin });
        Assert.True(foreignRaw.IsError);
        Assert.Equal("invalid_argument", Code(foreign));
    }

    [Fact]
    public async Task Connections_overview_filters_the_sources_that_need_attention()
    {
        Require();
        var bank = f.BankOf[f.North.TenantId];
        await f.Host.InTenantAsync(f.North.TenantId, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                var c = db.Connections.Single(x => x.Id == bank);
                c.SetOnboarding(OnboardingStatus.WaitingForAccess, "заявление в банк подано");
                c.MarkNeedsAttention("test: credentials rejected", f.Host.Time.GetUtcNow());
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return true;
        });
        try
        {
            await using var a = await f.ClientAsync(f.KeyA);
            var (all, _) = await f.CallAsync(a, "get_connections_overview");
            Assert.Equal(3, all.GetProperty("data").GetProperty("total").GetInt32());
            var (problems, raw) = await f.CallAsync(a, "get_connections_overview", new { only_needing_attention = true });
            Assert.True(raw.IsError != true, problems.GetRawText());
            var row = Assert.Single(problems.GetProperty("data").GetProperty("connections").EnumerateArray().ToList());
            Assert.Equal("302222222", row.GetProperty("organization").GetProperty("tin").GetString());
            var source = row.GetProperty("source");
            Assert.Equal("needs_attention", source.GetProperty("connection_status").GetString());
            Assert.Equal("waiting_for_access", source.GetProperty("onboarding_status").GetString());
            Assert.Equal("заявление в банк подано", source.GetProperty("onboarding_note").GetProperty("untrusted_text").GetString());
        }
        finally
        {
            await f.Host.InTenantAsync(f.North.TenantId, async sp =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                await db.InTransactionAsync(async ct =>
                {
                    var c = db.Connections.Single(x => x.Id == bank);
                    c.SetOnboarding(OnboardingStatus.NotStarted, null);
                    c.Activate(f.Host.Time.GetUtcNow());
                    await db.SaveChangesAsync(ct);
                }, CancellationToken.None);
                return true;
            });
        }
    }

    [Fact]
    public async Task Attention_items_of_all_organizations_carry_their_organization_and_page_without_gaps()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var (summary, _) = await f.CallAsync(a, "get_attention_summary", new { organization = "all" });
        var perOrg = summary.GetProperty("data").GetProperty("organizations").EnumerateArray().ToList();
        Assert.Equal(3, perOrg.Count);
        Assert.Equal("303333333", perOrg[0].GetProperty("organization").GetProperty("tin").GetString());
        var expected = summary.GetProperty("data").GetProperty("totals").GetProperty("open").GetInt32();

        var (whole, wholeRaw) = await f.CallAsync(a, "get_attention_items", new { organization = "all", limit = 200 });
        Assert.True(wholeRaw.IsError != true, whole.GetRawText());
        var items = whole.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected, items.Count);
        Assert.Equal("critical", items[0].GetProperty("item").GetProperty("severity").GetString());
        Assert.DoesNotContain(items, i => i.GetProperty("organization").GetProperty("tin").GetString() == "304444444");

        // Paging one by one returns exactly the same items in the same order.
        var paged = new List<Guid>();
        string? cursor = null;
        do
        {
            var (page, _) = await f.CallAsync(a, "get_attention_items", new { organization = "all", limit = 1, cursor });
            paged.AddRange(page.GetProperty("data").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("id").GetGuid()));
            cursor = page.GetProperty("next_cursor").GetString();
        } while (cursor is not null && paged.Count <= expected);
        Assert.Equal(items.Select(i => i.GetProperty("item").GetProperty("id").GetGuid()).ToList(), paged);

        // "all" is refused by tools that need one organization.
        var (refused, refusedRaw) = await f.CallAsync(a, "get_account_balances", new { organization = "all" });
        Assert.True(refusedRaw.IsError);
        Assert.Contains("get_portfolio_overview", refused.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Update_attention_item_applies_to_the_organization_of_the_item_with_the_review_scope()
    {
        Require();
        var item = await f.AddItemAsync(f.South.TenantId, Domain.Attention.AttentionSeverity.Medium, "test.update");
        await using (var readOnly = await f.ClientAsync(f.KeyAReadOnly))
        {
            var (denied, deniedRaw) = await f.CallAsync(readOnly, "update_attention_item", new { item_id = item.ToString(), action = "seen" });
            Assert.True(deniedRaw.IsError);
            Assert.Equal("forbidden", Code(denied));
        }
        await using var a = await f.ClientAsync(f.KeyA);
        var (body, raw) = await f.CallAsync(a, "update_attention_item", new { item_id = item.ToString(), action = "seen" });
        Assert.True(raw.IsError != true, body.GetRawText());
        Assert.Equal("303333333", body.GetProperty("organization").GetProperty("tin").GetString());
        Assert.Equal("seen", body.GetProperty("data").GetProperty("status").GetString());

        // Named organization that does not hold the item: not found there (nothing changes elsewhere).
        var (wrong, wrongRaw) = await f.CallAsync(a, "update_attention_item", new { item_id = item.ToString(), action = "reopen", organization = f.North.Tin });
        Assert.True(wrongRaw.IsError);
        Assert.Contains("No attention item", wrong.GetProperty("error").GetProperty("message").GetString());
        // An item of another account can never be found by A.
        await using var b = await f.ClientAsync(f.KeyB);
        var (foreign, foreignRaw) = await f.CallAsync(a, "update_attention_item", new { item_id = f.CriticalItem.ToString(), action = "seen", organization = "all" });
        Assert.True(foreignRaw.IsError);
    }

    /// <summary>
    /// docs/tool-selection-eval.md: every expected tool exists and its description has example questions in Russian, Uzbek
    /// Latin and Uzbek Cyrillic. Whether the model picks the tool is checked by hand at the demonstration, not here.
    /// </summary>
    [Fact]
    public async Task Tool_selection_eval_tools_exist_and_have_russian_and_uzbek_examples()
    {
        Require();
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Synect.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        var rows = File.ReadAllLines(Path.Combine(root, "docs", "tool-selection-eval.md"))
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\| \d+ \|")).ToList();
        Assert.Equal(40, rows.Count);
        var expected = rows.SelectMany(r => System.Text.RegularExpressions.Regex.Matches(r.Split('|')[4], "`([a-z_]+)`").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        var languages = rows.Select(r => r.Split('|')[3].Trim()).ToHashSet();
        Assert.Superset(new HashSet<string> { "ru", "uz-Latn", "uz-Cyrl", "mix" }, languages);

        await using var a = await f.ClientAsync(f.KeyA);
        var tools = (await a.ListToolsAsync()).ToDictionary(t => t.Name, t => t.Description ?? "");
        foreach (var tool in expected)
        {
            Assert.True(tools.TryGetValue(tool, out var description), $"{tool} does not exist");
            var examples = System.Text.RegularExpressions.Regex.Matches(description, "«([^»]+)»").Select(m => m.Groups[1].Value).ToList();
            Assert.True(examples.Any(IsRussian), $"{tool}: no Russian example");
            Assert.True(examples.Any(IsUzbekCyrillic), $"{tool}: no Uzbek Cyrillic example");
            Assert.True(examples.Any(IsUzbekLatin), $"{tool}: no Uzbek Latin example");
        }
    }

    private static bool HasCyrillic(string s) => s.Any(c => c is >= '\u0400' and <= '\u04FF');

    private static bool IsUzbekCyrillic(string s) => s.IndexOfAny(['ў', 'қ', 'ғ', 'ҳ', 'Ў', 'Қ', 'Ғ', 'Ҳ']) >= 0;

    private static bool IsRussian(string s) => HasCyrillic(s) && !IsUzbekCyrillic(s);

    /// <summary>Latin text with an Uzbek marker: o' / g' (any apostrophe), q, or a typical Uzbek suffix.</summary>
    private static bool IsUzbekLatin(string s) =>
        !HasCyrillic(s) && (s.IndexOfAny(['\'', 'ʻ', 'ʼ', '‘', '’', '`']) >= 0 || s.Contains('q', StringComparison.OrdinalIgnoreCase)
                            || System.Text.RegularExpressions.Regex.IsMatch(s, "(lar|ni|da|dagi|mi)\\b"));

    [Fact]
    public async Task Every_tool_lists_the_organization_parameter()
    {
        Require();
        await using var a = await f.ClientAsync(f.KeyA);
        var tools = await a.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "list_organizations");
        Assert.Contains(tools, t => t.Name == "get_portfolio_overview");
        Assert.Contains(tools, t => t.Name == "get_connections_overview");
        foreach (var tool in tools)
        {
            var property = tool.JsonSchema.GetProperty("properties").GetProperty("organization");
            Assert.Equal("string", property.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(property.GetProperty("description").GetString()), tool.Name);
            Assert.Contains("ORGANIZATIONS", tool.Description);
        }
    }
}
