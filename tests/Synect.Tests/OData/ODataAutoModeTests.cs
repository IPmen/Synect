using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Accounts;
using Synect.Application.Attention;
using Synect.Application.Ingest;
using Synect.Application.OData;
using Synect.Domain.Attention;

namespace Synect.Tests.OData;

/// <summary>
/// Auto mode per entity (OData:FullSyncInterval 24 h, FullSyncTolerance 2 h, FullRetryAfter 12 h), the canonical form of
/// numbers end to end, the per-entity full-export state in the connections overview and the connection.full_sync_overdue rule.
/// </summary>
public sealed partial class ODataSyncTests
{
    // The sample publishes Catalog_Номенклатура, so the optional categories and items load too; warehouses, price types and
    // the registers are not published and are skipped.
    private static readonly string[] AllEntities = ["counterparties", "contracts", "bank_accounts", "documents", "bank_documents", "categories", "items"];

    private Task<ODataSyncReport> AutoAsync(Guid tenant, Guid connection) =>
        Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>().RunAsync(connection, ODataSyncMode.Auto, false, CancellationToken.None));

    private static List<string> Attempted(ODataSyncReport r) => r.Entities.Where(e => e.FullAttempted).Select(e => e.Entity).ToList();

    [Fact]
    public async Task Another_form_of_the_same_number_with_the_same_DataVersion_is_unchanged_and_not_sent()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData number forms");
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Later();
        // Same values written differently, without a new DataVersion.
        server.Find("Document_РеализацияТоваровУслуг", FakeODataServer.Sale1)["СуммаДокумента"] = "1120000.00";
        server.Find("Document_ПоступлениеТоваровУслуг", FakeODataServer.Purchase1)["СуммаДокумента"] = "500.5000";
        var batches = await f.CountAsync("ingest_batches", tenant, "entity = 'documents'");

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var docs = E(report, "documents");
        Assert.Equal((2, 0, 0, 0), (docs.Unchanged, docs.Changed, docs.Sent, docs.Accepted));
        // Only the (empty) last package of the full export: no document was sent again.
        Assert.Equal(batches + 1, await f.CountAsync("ingest_batches", tenant, "entity = 'documents'"));
        Assert.Equal(0, await f.CountAsync("ingest_batches", tenant, "entity = 'documents' AND accepted > 0 AND received_at = (SELECT max(received_at) FROM ingest_batches)"));
    }

    [Fact]
    public async Task Nightly_only_schedule_gives_a_full_export_every_day()
    {
        RequirePostgres();
        var (tenant, connection, _) = await NewAsync("OData nightly only");
        Assert.Equal(AllEntities, Attempted(await AutoAsync(tenant, connection)));
        for (var day = 1; day <= 3; day++)
        {
            // The next nightly slot starts at the same time of day, i.e. a little less than 24 h after the previous load ended.
            Host.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10));
            var report = await AutoAsync(tenant, connection);
            Assert.Equal(AllEntities, Attempted(report));
            Assert.All(report.Entities.Where(e => e.Mode != "skipped"), e => Assert.Equal("full", e.Mode));
        }
    }

    [Fact]
    public async Task Default_schedule_gives_one_full_export_a_day_at_the_nightly_slot()
    {
        RequirePostgres();
        var (tenant, connection, _) = await NewAsync("OData default schedule");
        // Nightly slot, then daytime slots 6, 8, …, 16 h later (08:00–20:00 every 2 h), then the next nightly slot.
        var steps = new[] { 6, 2, 2, 2, 2, 2 }.Select(h => TimeSpan.FromHours(h)).ToList();
        for (var day = 0; day < 2; day++)
        {
            Assert.Equal(AllEntities, Attempted(await AutoAsync(tenant, connection)));
            foreach (var step in steps)
            {
                Host.Time.Advance(step);
                Assert.Empty(Attempted(await AutoAsync(tenant, connection)));
            }
            Host.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10) - TimeSpan.FromHours(16));
        }
        Assert.Equal(AllEntities, Attempted(await AutoAsync(tenant, connection)));
    }

    [Fact]
    public async Task An_entity_whose_full_export_never_completes_waits_for_the_retry_pause_and_does_not_hold_back_the_others()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData one entity stuck");
        server.NoCountFor.Add("Catalog_Контрагенты");
        var others = AllEntities.Skip(1).ToList();

        var first = await AutoAsync(tenant, connection);
        Assert.Equal(AllEntities, Attempted(first));
        Assert.Equal("delta", E(first, "counterparties").Mode);
        Assert.All(others, e => Assert.Equal("full", E(first, e).Mode));

        Host.Time.Advance(TimeSpan.FromHours(2));   // 2 h: everything recent or paused
        Assert.Empty(Attempted(await AutoAsync(tenant, connection)));
        Host.Time.Advance(TimeSpan.FromHours(10));  // 12 h after the failed attempt: counterparties again, the others not yet
        Assert.Equal(["counterparties"], Attempted(await AutoAsync(tenant, connection)));
        Host.Time.Advance(TimeSpan.FromHours(10));  // 22 h: the others are due (tolerance), counterparties paused (10 h)
        Assert.Equal(others, Attempted(await AutoAsync(tenant, connection)));
        Host.Time.Advance(TimeSpan.FromHours(2));   // 24 h: counterparties again (12 h after its last attempt)
        Assert.Equal(["counterparties"], Attempted(await AutoAsync(tenant, connection)));

        // The overview shows it per entity.
        var summary = (await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None))).Data!;
        var states = summary.Connections.Single(c => c.ConnectionId == connection).FullSyncs!;
        var cp = states.Single(s => s.Entity == "counterparties");
        Assert.Null(cp.LastFullSyncAt);
        Assert.NotNull(cp.LastAttemptAt);
        Assert.True(cp.LastAttemptFailed);
        Assert.Equal(ODataFullSyncStates.OutcomeIncompleteList, cp.LastOutcome);
        Assert.Contains("/$count", cp.LastReason);
        var docs = states.Single(s => s.Entity == "documents");
        Assert.NotNull(docs.LastFullSyncAt);
        Assert.False(docs.LastAttemptFailed);
    }

    [Fact]
    public async Task When_no_entity_completes_a_full_export_is_tried_at_most_once_per_retry_pause()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData nothing completes");
        server.OmitInlineCount = true;
        server.CountUnavailable = true;
        var attempts = AllEntities.ToDictionary(e => e, _ => new List<int>());
        for (var hour = 0; hour <= 26; hour += 2)
        {
            var report = await AutoAsync(tenant, connection);
            foreach (var e in Attempted(report)) attempts[e].Add(hour);
            Assert.All(report.Entities, e => Assert.Equal(0, e.MarkedDeleted));
            Host.Time.Advance(TimeSpan.FromHours(2));
        }
        Assert.All(attempts.Values, hours => Assert.Equal([0, 12, 24], hours));
    }

    // ---- connection.full_sync_overdue ----

    private Task<List<(string Status, string Details)>> OverdueAsync(Guid tenant) =>
        f.QueryAsync($"SELECT status, details::text FROM attention_items WHERE tenant_id = '{tenant}' AND rule_code = '{AttentionRuleCodes.ConnectionFullSyncOverdue}'",
            r => (r.GetString(0), r.GetString(1)));

    [Fact]
    public async Task Overdue_full_export_opens_an_item_that_deltas_do_not_close_and_a_completed_full_export_does()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData overdue");
        var extension = await Host.CreateTenantAsync("Extension overdue");
        await Host.InTenantAsync(extension, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None));
        server.NoCountFor.Add("Catalog_Контрагенты");
        await AutoAsync(tenant, connection);
        Assert.Empty(await OverdueAsync(tenant));

        // More than 3 days without a completed full export of counterparties; the others complete again in this load.
        Host.Time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(1));
        await AutoAsync(tenant, connection); // AfterLoadAsync evaluates the connection rules
        var open = Assert.Single(await OverdueAsync(tenant));
        Assert.Equal("New", open.Status);
        Assert.Equal("counterparties", System.Text.Json.JsonDocument.Parse(open.Details).RootElement.GetProperty("entity").GetString());
        Assert.Contains("incomplete_list", open.Details);
        Assert.Contains("/$count", open.Details);

        // A delta does not close it.
        Host.Time.Advance(TimeSpan.FromHours(2));
        await SyncAsync(tenant, connection, ODataSyncMode.Delta);
        Assert.NotEqual("Resolved", Assert.Single(await OverdueAsync(tenant)).Status);

        // A completed full export does (after the retry pause, in Auto).
        server.NoCountFor.Clear();
        Host.Time.Advance(TimeSpan.FromHours(12));
        var report = await AutoAsync(tenant, connection);
        Assert.Equal("full", E(report, "counterparties").Mode);
        Assert.Equal("Resolved", Assert.Single(await OverdueAsync(tenant)).Status);

        // The 1C extension never gets this rule.
        await Host.InTenantAsync(extension, sp => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync([AttentionCategory.Connection], CancellationToken.None));
        Assert.Empty(await OverdueAsync(extension));
    }

    [Fact]
    public async Task Entities_never_exported_in_full_are_shown_and_become_overdue_from_the_activation()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData first sync fails");
        // The connection's own profile: counterparties, contracts, bank accounts (in this order).
        var profile = System.Text.Json.Nodes.JsonNode.Parse(ODataProfile.BuiltIn["clobus-bp-uz"],
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!.AsObject();
        var three = new System.Text.Json.Nodes.JsonArray(profile["entities"]!.AsArray().Take(3).Select(e => e!.DeepClone()).ToArray());
        profile["name"] = "three-entities";
        profile["entities"] = three;
        await f.ExecAdminAsync($"UPDATE connections SET settings = settings || jsonb_build_object('profile_json', '{profile.ToJsonString().Replace("'", "''")}'::jsonb) WHERE id = '{connection}'");

        // The first load fails on the second entity: counterparties complete, contracts fail, bank accounts are never reached.
        server.FailWhen = (resource, query) => resource == "Catalog_ДоговорыКонтрагентов" && query.Contains("$select=Ref_Key,DataVersion", StringComparison.Ordinal)
            ? FakeODataServer.Status(System.Net.HttpStatusCode.ServiceUnavailable)
            : null;
        await Assert.ThrowsAsync<Synect.Application.Adapters.AdapterTransientException>(() => AutoAsync(tenant, connection));

        var states = (await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None))).Data!
            .Connections.Single(c => c.ConnectionId == connection).FullSyncs!;
        Assert.Equal(["counterparties", "contracts", "bank_accounts"], states.Select(x => x.Entity));
        Assert.NotNull(states[0].LastFullSyncAt);
        Assert.Equal((null, ODataFullSyncStates.OutcomeError), (states[1].LastFullSyncAt, states[1].LastOutcome));
        Assert.Equal((null, null), (states[2].LastFullSyncAt, states[2].LastAttemptAt)); // shown although the journal has nothing for it

        // Below the threshold: nothing; past it (counted from the activation, the connection's created_at here): both entities.
        Task Evaluate() => Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync([AttentionCategory.Connection], CancellationToken.None));
        // Two days later the scheduled load fails the same way; counterparties complete again, so only the other two can be overdue.
        Host.Time.Advance(TimeSpan.FromDays(2));
        await Assert.ThrowsAsync<Synect.Application.Adapters.AdapterTransientException>(() => AutoAsync(tenant, connection));
        await Evaluate();
        Assert.Empty(await OverdueAsync(tenant));
        Host.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        await Evaluate();
        var open = (await OverdueAsync(tenant)).Select(i => System.Text.Json.JsonDocument.Parse(i.Details).RootElement).ToList();
        Assert.Equal(["bank_accounts", "contracts"], open.Select(d => d.GetProperty("entity").GetString()).Order());
        Assert.All(open, d =>
        {
            Assert.True(d.GetProperty("never_completed").GetBoolean());
            Assert.StartsWith("the full export of this entity has never completed", d.GetProperty("last_failure").GetString());
        });
        Assert.Contains("AdapterTransientException", open.Single(d => d.GetProperty("entity").GetString() == "contracts").GetProperty("last_failure").GetString());
        Assert.Contains("never attempted", open.Single(d => d.GetProperty("entity").GetString() == "bank_accounts").GetProperty("last_failure").GetString());

        // A completed full export of both closes both items (the load itself re-evaluates the connection rules).
        server.FailWhen = null;
        var report = await AutoAsync(tenant, connection);
        Assert.Equal(("full", "full"), (E(report, "contracts").Mode, E(report, "bank_accounts").Mode));
        Assert.All(await OverdueAsync(tenant), i => Assert.Equal("Resolved", i.Status));
        Assert.Equal(2, (await OverdueAsync(tenant)).Count);
    }
}
