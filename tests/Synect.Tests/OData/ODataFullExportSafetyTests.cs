using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Adapters;
using Synect.Application.Attention;
using Synect.Application.Ingest;
using Synect.Application.OData;
using Synect.Domain.Attention;
using Synect.Tests.Postgres;

namespace Synect.Tests.OData;

/// <summary>
/// A full export marks records deleted only when the list of keys is known to be complete and the deletion is not massive
/// (OData:MassDeleteGuard; the fixture lowers MinCount to 5). Attention rules for OData connections.
/// </summary>
public sealed partial class ODataSyncTests
{
    private Task<string?> IsDeletedAsync(Guid tenant, string table, string refKey) =>
        f.ValueAsync($"SELECT is_deleted FROM {table} WHERE tenant_id = '{tenant}' AND ref = '{refKey}'");

    private Task<long> DeletedCountAsync(Guid tenant) => f.CountAsync("accounting_counterparties", tenant, "is_deleted");

    private static bool IsKeyListing(string resource, string query) =>
        resource == "Catalog_Контрагенты" && query.Contains("$select=Ref_Key,DataVersion", StringComparison.Ordinal);

    /// <summary>A tenant whose counterparties are the 3 of the sample plus <paramref name="extra"/> more, loaded in full once.</summary>
    private async Task<(Guid Tenant, Guid Connection, FakeODataServer Server, List<string> Extra)> LoadedAsync(string name, int extra = 0)
    {
        var (tenant, connection, server) = await NewAsync(name);
        var refs = new List<string>();
        for (var i = 0; i < extra; i++)
            refs.Add((string)server.Add("Catalog_Контрагенты", FakeODataServer.Counterparty(Guid.NewGuid().ToString("D"), $"Extra {i}", "30200" + i.ToString("0000"), "ЮридическоеЛицо"))["Ref_Key"]!);
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Later();
        return (tenant, connection, server, refs);
    }

    [Fact]
    public async Task A_an_error_on_a_page_of_keys_after_the_retries_marks_nothing_deleted()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData page error");
        var lastBatchesBefore = await f.CountAsync("ingest_batches", tenant, "entity = 'counterparties' AND is_last_batch");
        server.FailWhen = (resource, query) => IsKeyListing(resource, query) && query.Contains("$skip=2", StringComparison.Ordinal)
            ? FakeODataServer.Status(HttpStatusCode.ServiceUnavailable)
            : null;

        await Assert.ThrowsAsync<AdapterTransientException>(() => SyncAsync(tenant, connection, ODataSyncMode.Full));
        Assert.Equal(0, await DeletedCountAsync(tenant));
        Assert.Equal(lastBatchesBefore, await f.CountAsync("ingest_batches", tenant, "entity = 'counterparties' AND is_last_batch"));
        Assert.Equal("Failed", await f.ValueAsync($"SELECT result FROM sync_runs WHERE connection_id = '{connection}' ORDER BY started_at DESC, id DESC LIMIT 1"));
    }

    [Fact]
    public async Task B_a_count_that_changes_between_pages_sends_a_delta()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData count changes");
        var removed = false;
        server.OnRequest = (resource, query) =>
        {
            if (removed || !IsKeyListing(resource, query) || !query.Contains("$skip=2", StringComparison.Ordinal)) return;
            removed = true;
            server.Remove("Catalog_Контрагенты", FakeODataServer.CpC);
        };

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var cp = E(report, "counterparties");
        Assert.Equal(("delta", 0), (cp.Mode, cp.MarkedDeleted));
        Assert.False(report.Complete);
        Assert.Contains(cp.Warnings, w => w.Contains("odata.count changed", StringComparison.Ordinal));
        Assert.Equal("False", await IsDeletedAsync(tenant, "accounting_counterparties", FakeODataServer.CpC));
        Assert.Equal("False", await f.ValueAsync($"SELECT is_complete FROM sync_runs WHERE id = '{report.SyncRunId}'"));
    }

    [Fact]
    public async Task C_a_silently_truncated_page_without_a_count_is_not_taken_for_the_whole_list()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData truncated page");

        // The server returns 1 record where 2 were asked and says nothing about the total.
        server.MaxPageSize = 1;
        server.OmitInlineCount = true;
        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);

        Assert.Equal(0, E(report, "counterparties").MarkedDeleted);
        Assert.Equal("False", await IsDeletedAsync(tenant, "accounting_counterparties", FakeODataServer.CpB));
        Assert.Equal("False", await IsDeletedAsync(tenant, "accounting_counterparties", FakeODataServer.CpC));
        Assert.Equal("delta", E(report, "counterparties").Mode);
        Assert.False(report.Complete);
        Assert.Contains(E(report, "counterparties").Warnings, w => w.Contains("/$count says 3", StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.Resource == "Catalog_Контрагенты/$count");
    }

    [Fact]
    public async Task C2_without_a_count_and_without_count_endpoint_the_export_is_a_delta()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData no count at all");
        server.OmitInlineCount = true;
        server.CountUnavailable = true;
        server.Remove("Catalog_Контрагенты", FakeODataServer.CpC);

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal(("delta", 0), (E(report, "counterparties").Mode, E(report, "counterparties").MarkedDeleted));
        Assert.Contains(E(report, "counterparties").Warnings, w => w.Contains("/$count is not available", StringComparison.Ordinal));
        Assert.Equal("False", await IsDeletedAsync(tenant, "accounting_counterparties", FakeODataServer.CpC));

        // With a working /$count that agrees, the same export completes and marks the absent record.
        server.CountUnavailable = false;
        Later();
        var full = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal(("full", 1), (E(full, "counterparties").Mode, E(full, "counterparties").MarkedDeleted));
    }

    [Fact]
    public async Task D_an_empty_answer_with_count_zero_does_not_delete_the_stored_records()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData empty answer");
        server.Resources["Catalog_Контрагенты"].Clear();

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var cp = E(report, "counterparties");
        Assert.True(cp.MassDeleteBlocked); // only 3 records, below MinCount: an empty source is held back anyway
        Assert.Equal((0, 3, 3), (cp.MarkedDeleted, cp.MissingInSource, cp.ActiveStored));
        Assert.False(report.Complete);
        Assert.Equal(0, await DeletedCountAsync(tenant));
        var marker = await f.ValueAsync($"""
            SELECT envelope_extra -> 'synect_odata' -> 'mass_delete_blocked' ->> 'empty_source' FROM ingest_batches
            WHERE tenant_id = '{tenant}' AND entity = 'counterparties' AND (envelope_extra -> 'synect_odata' -> 'mass_delete_blocked') IS NOT NULL
            """);
        Assert.Equal("true", marker);
    }

    [Fact]
    public async Task E_a_usual_export_with_a_few_absent_records_marks_them_as_before()
    {
        RequirePostgres();
        var (tenant, connection, server, extra) = await LoadedAsync("OData few deletions", extra: 20);
        foreach (var r in extra.Take(4)) server.Remove("Catalog_Контрагенты", r);

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var cp = E(report, "counterparties");
        Assert.Equal(("full", 4, false), (cp.Mode, cp.MarkedDeleted, cp.MassDeleteBlocked)); // 4 < MinCount 5
        Assert.True(report.Complete);
        Assert.Equal(4, await DeletedCountAsync(tenant));
    }

    [Fact]
    public async Task F_the_guard_threshold_and_the_explicit_confirmation()
    {
        RequirePostgres();
        Assert.Equal((true, 0.2, 50), (new ODataMassDeleteGuardOptions().Enabled, new ODataMassDeleteGuardOptions().MaxShare, new ODataMassDeleteGuardOptions().MinCount));

        // 28 active; 5 absent = 17.9%: at least MinCount but not more than 20% — marked as usual.
        var (t1, c1, s1, extra1) = await LoadedAsync("OData guard below share", extra: 25);
        foreach (var r in extra1.Take(5)) s1.Remove("Catalog_Контрагенты", r);
        Assert.Equal(5, E(await SyncAsync(t1, c1, ODataSyncMode.Full), "counterparties").MarkedDeleted);

        // 28 active; 6 absent = 21.4% and at least 5: held back.
        var (tenant, connection, server, extra) = await LoadedAsync("OData guard above share", extra: 25);
        foreach (var r in extra.Take(6)) server.Remove("Catalog_Контрагенты", r);
        var held = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal((true, 0, 6, 28), (E(held, "counterparties").MassDeleteBlocked, E(held, "counterparties").MarkedDeleted,
            E(held, "counterparties").MissingInSource, E(held, "counterparties").ActiveStored));
        Assert.Equal(0, await DeletedCountAsync(tenant));
        Assert.Contains(Host.Logs.Lines, l => l.Contains("mass deletion held back", StringComparison.OrdinalIgnoreCase) && l.Contains("counterparties", StringComparison.Ordinal));

        // A blank operator is not a confirmation; a named one lets the export complete and is journaled.
        await Assert.ThrowsAsync<ArgumentException>(() => Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>()
            .RunAsync(connection, ODataSyncMode.Full, false, CancellationToken.None, " ")));
        Later();
        var confirmed = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>()
            .RunAsync(connection, ODataSyncMode.Full, false, CancellationToken.None, "tester@devbox"));
        Assert.Equal((true, 6), (E(confirmed, "counterparties").MassDeleteConfirmed, E(confirmed, "counterparties").MarkedDeleted));
        Assert.Equal(6, await DeletedCountAsync(tenant));
        var audit = await f.ValueAsync($"""
            SELECT envelope_extra -> 'synect_odata' -> 'mass_delete_confirmed' ->> 'confirmed_by' FROM ingest_batches
            WHERE tenant_id = '{tenant}' AND (envelope_extra -> 'synect_odata' -> 'mass_delete_confirmed') IS NOT NULL
            """);
        Assert.Equal("tester@devbox", audit);
        Assert.Contains(Host.Logs.Lines, l => l.Contains("CONFIRMED by tester@devbox", StringComparison.Ordinal));

        // The queue never confirms: it has no way to pass an operator (SyncAsync takes none).
    }

    // ---- attention ----

    private Task<List<(string Rule, string Details)>> ItemsAsync(Guid tenant) =>
        f.QueryAsync($"SELECT rule_code, details::text FROM attention_items WHERE tenant_id = '{tenant}' AND status NOT IN ('Resolved', 'Archived')",
            r => (r.GetString(0), r.GetString(1)));

    [Fact]
    public async Task Attention_runs_after_an_OData_load_and_reports_an_incomplete_one_with_the_held_back_deletion()
    {
        RequirePostgres();
        var (tenant, connection, server, _) = await LoadedAsync("OData attention incomplete");
        server.Resources["Catalog_Контрагенты"].Clear();
        Host.Time.Advance(TimeSpan.FromHours(25)); // past OData:FullSyncInterval: the queued (Auto) load is a full one

        // The queue path: AfterLoadAsync is called by the load itself, no manual evaluation.
        var outcome = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>().SyncAsync(connection, CancellationToken.None));
        Assert.False(outcome.IsComplete);
        var items = await ItemsAsync(tenant);
        var incomplete = Assert.Single(items, i => i.Rule == AttentionRuleCodes.ConnectionIncompleteLoad);
        Assert.Contains("onec_fresh_odata", incomplete.Details);
        Assert.Contains("mass_delete_blocked", incomplete.Details);
        Assert.Contains("counterparties", incomplete.Details);
    }

    [Fact]
    public async Task Stale_and_incomplete_rules_cover_OData_but_not_the_1C_extension()
    {
        RequirePostgres();
        var (odataTenant, _, _, _) = await LoadedAsync("OData attention stale");
        var extensionTenant = await Host.CreateTenantAsync("Extension attention stale");
        await Host.InTenantAsync(extensionTenant, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None));

        Host.Time.Advance(TimeSpan.FromHours(40)); // past Attention:StaleAfter (36 h) for both
        foreach (var t in new[] { odataTenant, extensionTenant })
            await Host.InTenantAsync(t, sp => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync([AttentionCategory.Connection], CancellationToken.None));

        Assert.Contains(await ItemsAsync(odataTenant), i => i.Rule == AttentionRuleCodes.ConnectionStaleData && i.Details.Contains("onec_fresh_odata"));
        var extension = await ItemsAsync(extensionTenant);
        Assert.DoesNotContain(extension, i => i.Rule == AttentionRuleCodes.ConnectionStaleData);
        Assert.DoesNotContain(extension, i => i.Rule == AttentionRuleCodes.ConnectionIncompleteLoad);
    }

    // ---- VAT ----

    [Fact]
    public async Task Vat_rate_6_and_6_106_are_stored_as_6()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData VAT 6");
        server.Update("Document_РеализацияТоваровУслуг", FakeODataServer.Sale1, r =>
        {
            r["Товары"]![0]!["СтавкаНДС"] = "НДС6";
            r["Услуги"]![0]!["СтавкаНДС"] = "НДС6_106";
        });
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var rates = await f.QueryAsync($"""
            SELECT l.vat_rate::text FROM accounting_document_lines l
            WHERE l.tenant_id = '{tenant}' AND l.document_ref = '{FakeODataServer.Sale1}' ORDER BY l.line_no
            """, r => r.GetString(0));
        Assert.Equal([6m, 6m], rates.Select(r => decimal.Parse(r, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
