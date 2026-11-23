using System.Net;
using System.Text.Json;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Postgres;

namespace Synect.Tests.Ingest;

/// <summary>What the receiver stores: validation per item, idempotency, order, deletions, full exports, extra fields, amounts.</summary>
public class IngestDataTests(IngestFixture f) : IClassFixture<IngestFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(5)).AddMinutes(minutes);

    private static Dictionary<string, object?> Cp(string r, string name = "Name", string? tin = "301000001", int at = 0) =>
        OneCSampleClient.Counterparty(r, name, tin, T(at));

    private static List<(string Status, string? Code)> Items(SampleResponse r) => r.Items.ToList();

    [Fact]
    public async Task Item_errors_are_reported_per_item_and_do_not_stop_the_others()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest validation");
        var client = f.Client(org);
        var noName = Cp("v-noname");
        noName.Remove("name");
        var longRef = Cp(new string('r', 65));
        var badDate = Cp("v-date");
        badDate["modified_at"] = "01.11.2026";
        var objectName = Cp("v-obj");
        objectName["name"] = new { first = "a" };
        var nul = Cp("v-nul", "bad\u0000name");

        var r = await client.SendAsync(client.Envelope("counterparties", "val-1", 1, new object[]
        {
            Cp("v-ok"), Cp("v-tin", tin: "1234567890"), Cp(""), Cp("00000000-0000-0000-0000-000000000000"), longRef, noName, badDate, objectName, nul,
        }));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("partially_accepted", r.Get("status"));
        Assert.Equal(
        [
            ("accepted", null), ("rejected", "invalid_tin"), ("rejected", "empty_ref"), ("rejected", "empty_ref"), ("rejected", "ref_too_long"),
            ("rejected", "missing_field"), ("rejected", "invalid_timestamp"), ("rejected", "invalid_value"), ("rejected", "invalid_text"),
        ], Items(r));
        Assert.Contains("name", IngestFixture.Item(r.Body, 5).GetProperty("message").GetString());
        Assert.Equal(1L, await f.CountAsync("accounting_counterparties", org.TenantId));
        Assert.Equal("ProcessedWithErrors", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'val-1'", "status"));
        Assert.Equal("8", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'val-1'", "rejected"));
        Assert.Equal("invalid_tin", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'val-1'", "item_errors->0->>'code'"));

        // Documents: dates, amounts and required fields are checked per item too.
        var doc = (string reference) => OneCSampleClient.Document(reference, "Sales", "cp", null, 100m, T(0));
        var badDocDate = doc("d-date");
        badDocDate["date"] = "2026-13-45";
        var fractional = doc("d-frac");
        fractional["amount_total"] = 10.001m;
        var badKind = doc("d-kind");
        badKind["doc_kind"] = "Barter";
        var badLine = doc("d-line");
        ((List<Dictionary<string, object?>>)badLine["lines"]!)[1]["amount"] = "abc";
        var docs = await client.SendAsync(client.Envelope("documents", "val-2", 1, new object[] { doc("d-ok"), badDocDate, fractional, badKind, badLine }));
        Assert.Equal(
            [("accepted", null), ("rejected", "invalid_date"), ("rejected", "fractional_minor_units"), ("rejected", "invalid_value"), ("rejected", "invalid_amount")],
            Items(docs));
        Assert.Contains("lines[2]", IngestFixture.Item(docs.Body, 4).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Repeated_package_is_a_duplicate_and_a_different_one_under_the_same_id_is_a_conflict()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest duplicate");
        var client = f.Client(org);
        var envelope = client.Envelope("counterparties", "dup-1", 1, new object[] { Cp("d1"), Cp("d2"), Cp("", "bad") });
        var first = await client.SendAsync(envelope);
        Assert.Equal("partially_accepted", first.Get("status"));
        var updatedAt = await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd1'", "updated_at");

        var again = await client.SendAsync(envelope);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("duplicate", again.Get("status"));
        Assert.Equal(2, again.Int("accepted"));
        Assert.Equal(1, again.Int("rejected"));
        Assert.Contains("empty_ref", again.Get("rejected_items"));
        Assert.Equal(1L, await f.CountAsync("ingest_batches", org.TenantId, "batch_id = 'dup-1'"));
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'dup-1'", "attempts"));
        Assert.Equal(updatedAt, await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd1'", "updated_at"));

        var different = await client.SendAsync(client.Envelope("counterparties", "dup-1", 1, new object[] { Cp("d1", "Changed", at: 10) }));
        Assert.Equal(HttpStatusCode.Conflict, different.Status);
        Assert.Equal("batch_conflict", different.Get("error"));
        Assert.Equal("Name", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd1'", "name"));
    }

    [Fact]
    public async Task Late_package_never_overwrites_newer_data_and_unchanged_records_are_not_rewritten()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest order");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("counterparties", "ord-1", 1, new object[] { Cp("o1", "New name", at: 60) }));
        var stored = await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "updated_at");
        var batch = await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "last_batch_id");

        var late = await client.SendAsync(client.Envelope("counterparties", "ord-2", 1, new object[] { Cp("o1", "Old name", at: 0) }));
        Assert.Equal([("unchanged", "older_than_stored")], Items(late));
        Assert.Equal("New name", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "name"));

        var same = await client.SendAsync(client.Envelope("counterparties", "ord-3", 1, new object[] { Cp("o1", "New name", at: 60) }));
        Assert.Equal([("unchanged", "same_content")], Items(same));
        Assert.Equal(stored, await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "updated_at"));
        Assert.Equal(batch, await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "last_batch_id"));

        // The same modified_at with different content is applied (a correction without a new change time).
        var corrected = await client.SendAsync(client.Envelope("counterparties", "ord-4", 1, new object[] { Cp("o1", "Corrected", at: 60) }));
        Assert.Equal([("accepted", null)], Items(corrected));
        Assert.Equal("Corrected", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "name"));

        // Twice in one package: the later version wins.
        var twice = await client.SendAsync(client.Envelope("counterparties", "ord-5", 1, new object[] { Cp("o2", "First", at: 10), Cp("o2", "Second", at: 20) }));
        Assert.Equal([("accepted", null), ("accepted", null)], Items(twice));
        Assert.Equal("Second", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o2'", "name"));
    }

    [Fact]
    public async Task Document_lines_and_settlements_are_replaced_as_a_whole()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest lines");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("documents", "lin-1", 1, new object[] { OneCSampleClient.Document("doc-l", "Sales", "cp", null, 300m, T(0), lines: 3) }));
        Assert.Equal(3L, await f.CountAsync("accounting_document_lines", org.TenantId, "document_ref = 'doc-l'"));

        var r = await client.SendAsync(client.Envelope("documents", "lin-2", 1, new object[] { OneCSampleClient.Document("doc-l", "Sales", "cp", null, 100m, T(10), lines: 1) }));
        Assert.Equal([("accepted", null)], Items(r));
        Assert.Equal(1L, await f.CountAsync("accounting_document_lines", org.TenantId, "document_ref = 'doc-l'"));
        Assert.Equal("10000", await f.ValueAsync("accounting_document_lines", org.TenantId, "document_ref = 'doc-l'", "amount_minor"));
        Assert.Equal("10000", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'doc-l'", "amount_total_minor"));

        // Without the lines field the stored lines stay; an empty array removes them.
        var noLines = OneCSampleClient.Document("doc-l", "Sales", "cp", null, 100m, T(20));
        noLines.Remove("lines");
        await client.SendAsync(client.Envelope("documents", "lin-3", 1, new object[] { noLines }));
        Assert.Equal(1L, await f.CountAsync("accounting_document_lines", org.TenantId, "document_ref = 'doc-l'"));
        noLines["lines"] = Array.Empty<object>();
        noLines["modified_at"] = "2026-11-01T10:00:00";
        await client.SendAsync(client.Envelope("documents", "lin-4", 1, new object[] { noLines }));
        Assert.Equal(0L, await f.CountAsync("accounting_document_lines", org.TenantId, "document_ref = 'doc-l'"));

        await client.SendAsync(client.Envelope("bank_documents", "set-1", 1, new object[] { OneCSampleClient.BankDocument("bd-s", "Receipt", "cp", null, 50m, T(0), "doc-l") }));
        Assert.Equal("doc-l", await f.ValueAsync("accounting_bank_document_settlements", org.TenantId, "document_ref = 'bd-s'", "settled_document_ref"));
        var changed = OneCSampleClient.BankDocument("bd-s", "Receipt", "cp", null, 50m, T(10));
        changed["settlements"] = new[] { new { settled_document_ref = "doc-a", amount = 20 }, new { settled_document_ref = "doc-b", amount = 30 } };
        await client.SendAsync(client.Envelope("bank_documents", "set-2", 1, new object[] { changed }));
        Assert.Equal(2L, await f.CountAsync("accounting_bank_document_settlements", org.TenantId, "document_ref = 'bd-s'"));
        Assert.Equal(0L, await f.CountAsync("accounting_bank_document_settlements", org.TenantId, "settled_document_ref = 'doc-l'"));
    }

    [Fact]
    public async Task Deletions_and_unposting_are_marked_and_nothing_is_physically_removed()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest deletions");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("documents", "del-1", 1, new object[]
        {
            OneCSampleClient.Document("dd-1", "Sales", "cp", null, 10m, T(0)), OneCSampleClient.Document("dd-2", "Sales", "cp", null, 20m, T(0)),
        }));

        var r = await client.SendAsync(client.Envelope("documents", "del-2", 1, new object[]
        {
            new Dictionary<string, object?> { ["ref"] = "dd-1", ["is_deleted"] = true, ["modified_at"] = "2026-11-01T12:00:00" },
            OneCSampleClient.Document("dd-2", "Sales", "cp", null, 20m, T(60), posted: false),
            new Dictionary<string, object?> { ["ref"] = "dd-unknown", ["is_deleted"] = true },
            new Dictionary<string, object?> { ["ref"] = "dd-partial" }, // not a deletion: required fields are missing
        }));
        Assert.Equal([("accepted", null), ("accepted", null), ("unchanged", "unknown_ref_deleted"), ("rejected", "missing_field")], Items(r));
        Assert.Equal("true", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'dd-1'", "is_deleted"));
        Assert.Equal("N-dd-1", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'dd-1'", "number"));
        Assert.Equal(2L, await f.CountAsync("accounting_document_lines", org.TenantId, "document_ref = 'dd-1'"));
        Assert.Equal("false", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'dd-2'", "posted"));
        Assert.Equal(2L, await f.CountAsync("accounting_documents", org.TenantId));

        // A late full version older than the deletion does not bring the document back.
        var late = await client.SendAsync(client.Envelope("documents", "del-3", 1, new object[] { OneCSampleClient.Document("dd-1", "Sales", "cp", null, 10m, T(30)) }));
        Assert.Equal([("unchanged", "older_than_stored")], Items(late));
        Assert.Equal("true", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'dd-1'", "is_deleted"));
    }

    [Fact]
    public async Task Full_export_marks_missing_records_only_after_a_complete_export_with_is_last_batch()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest full");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("counterparties", "fl-0", 1, new object[] { Cp("f1"), Cp("f2"), Cp("f3"), Cp("f4") }));

        // Truncated: no is_last_batch, nothing is marked.
        var truncated = await client.SendAsync(client.Envelope("counterparties", "fl-1", 1, new object[] { Cp("f1") }, "full", "FS-1"));
        Assert.Equal(0, truncated.Int("marked_deleted"));
        Assert.Contains("waiting_for_last_batch", truncated.Get("full_sync"));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", org.TenantId, "is_deleted"));

        // Sequence 2 is lost: the last batch (3) marks nothing and says what is missing.
        await client.SendAsync(client.Envelope("counterparties", "fl-2", 1, new object[] { Cp("f1") }, "full", "FS-2", false));
        var gap = await client.SendAsync(client.Envelope("counterparties", "fl-2", 3, new object[] { Cp("f2") }, "full", "FS-2", true));
        Assert.Equal(0, gap.Int("marked_deleted"));
        Assert.Contains("missing_sequences", gap.Get("full_sync"));
        Assert.Contains("[2]", gap.Get("full_sync"));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", org.TenantId, "is_deleted"));

        // Complete: f1, f2 (refused but existing in 1C) and new f5 are in it; f3 and f4 are marked deleted.
        await client.SendAsync(client.Envelope("counterparties", "fl-3", 1, new object[] { Cp("f1"), Cp("f2", tin: "bad") }, "full", "FS-3", false));
        var done = await client.SendAsync(client.Envelope("counterparties", "fl-3", 2, new object[] { Cp("f5") }, "full", "FS-3", true));
        Assert.Equal(2, done.Int("marked_deleted"));
        Assert.Contains("\"completed\":true", done.Get("full_sync"));
        Assert.Equal(["f3", "f4"], await f.QueryAdminAsync(
            $"SELECT ref FROM accounting_counterparties WHERE tenant_id = '{org.TenantId}' AND is_deleted ORDER BY ref", x => x.GetString(0)));
        Assert.Equal(5L, await f.CountAsync("accounting_counterparties", org.TenantId));
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'fl-3' AND sequence = 2", "marked_deleted"));
        Assert.Equal(0L, await f.CountAsync("ingest_full_sync_items", org.TenantId, "full_sync_id = 'FS-3'"));
        // Other entities are not touched by a counterparties export.
        await client.SendAsync(client.Envelope("contracts", "fl-c", 1, new object[] { OneCSampleClient.Contract("c1", "f1", T(0)) }));
        await client.SendAsync(client.Envelope("counterparties", "fl-4", 1, new object[] { Cp("f1") }, "full", "FS-4", true));
        Assert.Equal(0L, await f.CountAsync("accounting_contracts", org.TenantId, "is_deleted"));
    }

    [Fact]
    public async Task A_record_marked_deleted_by_a_full_export_is_restored_when_the_same_item_comes_again()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest restore");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("counterparties", "rs-0", 1, new object[] { Cp("r1", at: 10), Cp("r2", at: 10) }));

        // A full export without r1 marks it deleted; its content hash stays.
        var full = await client.SendAsync(client.Envelope("counterparties", "rs-1", 1, new object[] { Cp("r2", at: 10) }, "full", "RS-1", true));
        Assert.Equal(1, full.Int("marked_deleted"));
        Assert.Equal("true", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'r1'", "is_deleted"));

        // Exactly the same item again: accepted with code restored, the mark is lifted.
        var again = await client.SendAsync(client.Envelope("counterparties", "rs-2", 1, new object[] { Cp("r1", at: 10) }));
        Assert.Equal([("accepted", "restored")], Items(again));
        Assert.Equal("accepted", again.Get("status"));
        Assert.Equal("false", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'r1'", "is_deleted"));
        Assert.Equal("Name", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'r1'", "name"));

        // Once restored, the same item is simply unchanged.
        var third = await client.SendAsync(client.Envelope("counterparties", "rs-3", 1, new object[] { Cp("r1", at: 10) }));
        Assert.Equal([("unchanged", "same_content")], Items(third));
    }

    [Fact]
    public async Task Restoring_never_applies_an_older_version()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest restore older");
        var client = f.Client(org);
        // Without modified_at the change time is exported_at of the package, so the same content can come with an older time.
        var noTime = Cp("o1");
        noTime.Remove("modified_at");
        await client.SendAsync(client.Envelope("counterparties", "ro-0", 1, new object[] { noTime, Cp("o2") }, exportedAt: T(30)));
        await client.SendAsync(client.Envelope("counterparties", "ro-1", 1, new object[] { Cp("o2") }, "full", "RO-1", true));
        Assert.Equal("true", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "is_deleted"));

        // Same content, older time: not applied, the mark stays.
        var older = await client.SendAsync(client.Envelope("counterparties", "ro-2", 1, new object[] { noTime }, exportedAt: T(20)));
        Assert.Equal([("unchanged", "same_content")], Items(older));
        Assert.Equal("true", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "is_deleted"));

        // Same content, not older: restored.
        var newer = await client.SendAsync(client.Envelope("counterparties", "ro-3", 1, new object[] { noTime }, exportedAt: T(40)));
        Assert.Equal([("accepted", "restored")], Items(newer));
        Assert.Equal("false", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "is_deleted"));

        // Changed content with an older modified_at than stored: not applied either.
        var stale = await client.SendAsync(client.Envelope("counterparties", "ro-4", 1, new object[] { Cp("o1", name: "Old name", at: 0) }));
        Assert.Equal([("unchanged", "older_than_stored")], Items(stale));
        Assert.Equal("Name", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'o1'", "name"));
    }

    /// <summary>
    /// Documents the CURRENT behaviour after an explicit deletion from 1C (not a restore case: a deletion stores the hash of
    /// the deletion item, which contains is_deleted = true, so an item without the deletion never has the same hash).
    /// </summary>
    [Fact]
    public async Task After_an_explicit_deletion_the_same_fields_without_the_deletion_are_an_ordinary_change()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest deletion then same");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("counterparties", "dx-0", 1, new object[] { Cp("d1", at: 10), Cp("d2", at: 10) }));

        // d1: full item with is_deleted = true; d2: deletion notice (ref only).
        var deleted = Cp("d1", at: 20);
        deleted["is_deleted"] = true;
        var deletion = await client.SendAsync(client.Envelope("counterparties", "dx-1", 1, new object[]
        {
            deleted,
            new Dictionary<string, object?> { ["ref"] = "d2", ["is_deleted"] = true, ["modified_at"] = "2026-11-01T09:20:00" },
        }));
        Assert.Equal([("accepted", null), ("accepted", null)], Items(deletion));

        // The same fields without the deletion at the SAME modified_at: a different hash, not older -> applied as a change,
        // the record is not deleted any more (code null, not "restored").
        var same = await client.SendAsync(client.Envelope("counterparties", "dx-2", 1, new object[] { Cp("d1", at: 20), Cp("d2", at: 20) }));
        Assert.Equal([("accepted", null), ("accepted", null)], Items(same));
        Assert.Equal("false", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd1'", "is_deleted"));
        Assert.Equal("false", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd2'", "is_deleted"));

        // The version from before the deletion (older modified_at) is not applied.
        var deletedAgain = Cp("d1", at: 30);
        deletedAgain["is_deleted"] = true;
        await client.SendAsync(client.Envelope("counterparties", "dx-3", 1, new object[] { deletedAgain }));
        var before = await client.SendAsync(client.Envelope("counterparties", "dx-4", 1, new object[] { Cp("d1", at: 10) }));
        Assert.Equal([("unchanged", "older_than_stored")], Items(before));
        Assert.Equal("true", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'd1'", "is_deleted"));
    }

    [Fact]
    public async Task Full_export_of_balances_marks_only_rows_of_the_dates_it_covers()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest balance full");
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("settlement_balances", "bf-0", 1, new object[]
        {
            OneCSampleClient.SettlementBalance("2026-09-30", "c1", null, "4010", 1m),
            OneCSampleClient.SettlementBalance("2026-10-31", "c1", null, "4010", 2m),
            OneCSampleClient.SettlementBalance("2026-10-31", "c2", "k2", "4010", 3m),
        }));
        var r = await client.SendAsync(client.Envelope("settlement_balances", "bf-1", 1, new object[]
        {
            OneCSampleClient.SettlementBalance("2026-10-31", "c1", null, "4010", 5m),
        }, "full", "BF-1", true));
        Assert.Equal(1, r.Int("marked_deleted"));
        Assert.Equal("true", await f.ValueAsync("accounting_settlement_balances", org.TenantId, "counterparty_ref = 'c2'", "is_deleted"));
        Assert.Equal("false", await f.ValueAsync("accounting_settlement_balances", org.TenantId, "as_of_date = '2026-09-30'", "is_deleted"));
        Assert.Equal("500", await f.ValueAsync("accounting_settlement_balances", org.TenantId, "as_of_date = '2026-10-31' AND counterparty_ref = 'c1'", "balance_debit_minor"));
        // NULL contract_ref is part of the key: the same row was updated, not duplicated.
        Assert.Equal(3L, await f.CountAsync("accounting_settlement_balances", org.TenantId));
    }

    [Fact]
    public async Task Unknown_fields_are_kept_in_extra_and_the_whole_item_in_the_raw_layer()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest extra");
        var client = f.Client(org);
        var item = Cp("x1");
        item["ВидКонтрагента"] = "ЮрЛицо";
        item["custom"] = new { score = 7, tags = new[] { "a", "b" } };
        item["FullName"] = "Spelled differently"; // a known field in another spelling
        var envelope = client.Envelope("counterparties", "ext-1", 1, new object[] { item });
        envelope["extension_build"] = "2026.11.1";
        var r = await client.SendAsync(envelope);
        Assert.Equal([("accepted", null)], Items(r));

        Assert.Equal("ЮрЛицо", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'x1'", "extra->>'ВидКонтрагента'"));
        Assert.Equal("7", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'x1'", "extra->'custom'->>'score'"));
        Assert.Equal("Name MChJ", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'x1'", "full_name")); // first spelling wins
        Assert.Equal("Spelled differently", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'x1'", "extra->>'FullName'"));
        Assert.Null(await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'x1'", "extra->>'name'"));
        Assert.Equal("b", await f.ValueAsync("raw_objects", org.TenantId, "object_type = '1c.counterparties' AND external_id = 'x1'", "payload->'custom'->'tags'->>1"));
        Assert.Equal("Name", await f.ValueAsync("raw_objects", org.TenantId, "object_type = '1c.counterparties' AND external_id = 'x1'", "payload->>'name'"));
        Assert.Equal("2026.11.1", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ext-1'", "envelope_extra->>'extension_build'"));

        // Lines: unknown line fields and a VAT rate that is not a number go to the line's extra, never refused.
        var doc = OneCSampleClient.Document("dx", "Sales", "cp", null, 100m, T(0));
        ((List<Dictionary<string, object?>>)doc["lines"]!)[0]["unit"] = "шт";
        await client.SendAsync(client.Envelope("documents", "ext-2", 1, new object[] { doc }));
        Assert.Equal("шт", await f.ValueAsync("accounting_document_lines", org.TenantId, "document_ref = 'dx' AND line_no = 1", "extra->>'unit'"));
        Assert.Equal("12", await f.ValueAsync("accounting_document_lines", org.TenantId, "document_ref = 'dx' AND line_no = 1", "vat_rate::int"));
        Assert.Null(await f.ValueAsync("accounting_document_lines", org.TenantId, "document_ref = 'dx' AND line_no = 2", "vat_rate"));
        Assert.Equal("Без НДС", await f.ValueAsync("accounting_document_lines", org.TenantId, "document_ref = 'dx' AND line_no = 2", "extra->>'vat_rate'"));

        // A refused item with a readable ref is kept in the raw layer apart, for reprocessing after a contract fix.
        await client.SendAsync(client.Envelope("counterparties", "ext-3", 1, new object[] { Cp("x-bad", tin: "1") }));
        Assert.Equal("1", await f.ValueAsync("raw_objects", org.TenantId, "object_type = '1c.counterparties.rejected' AND external_id = 'x-bad'", "payload->>'tin'"));
        Assert.Equal(0L, await f.CountAsync("raw_objects", org.TenantId, "object_type = '1c.counterparties' AND external_id = 'x-bad'"));
    }

    [Fact]
    public async Task A_document_may_arrive_before_its_counterparty()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest early document");
        var client = f.Client(org);
        var doc = await client.SendAsync(client.Envelope("documents", "early-1", 1, new object[] { OneCSampleClient.Document("de", "Purchase", "cp-later", "301000009", 10m, T(0)) }));
        Assert.Equal([("accepted", null)], Items(doc));
        var contract = await client.SendAsync(client.Envelope("contracts", "early-2", 1, new object[] { OneCSampleClient.Contract("ce", "cp-later", T(0)) }));
        Assert.Equal([("accepted", null)], Items(contract));
        var cp = await client.SendAsync(client.Envelope("counterparties", "early-3", 1, new object[] { Cp("cp-later", tin: "301000009") }));
        Assert.Equal([("accepted", null)], Items(cp));
        Assert.Equal(1L, await f.ScalarAsync<long>(
            $"SELECT count(*) FROM accounting_documents d JOIN accounting_counterparties c ON c.tenant_id = d.tenant_id AND c.ref = d.counterparty_ref WHERE d.tenant_id = '{org.TenantId}'"));
    }

    [Fact]
    public async Task Amounts_are_stored_in_minor_units_and_currencies_as_letter_codes()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest amounts");
        var client = f.Client(org);
        Dictionary<string, object?> Order(string reference, object amount, object? currency)
        {
            var o = OneCSampleClient.PaymentOrder(reference, null, 1m, T(0));
            o["amount"] = amount;
            if (currency is null) o.Remove("currency");
            else o["currency"] = currency;
            return o;
        }
        var r = await client.SendAsync(client.Envelope("payment_orders", "amt-1", 1, new object[]
        {
            Order("a1", "1 234,50", "860"), Order("a2", 100, "usd"), Order("a3", "0.1", null), Order("a4", -5.25m, "сум"), Order("a5", 978, 978),
            Order("a6", "12.345", "UZS"), Order("a7", "1e3", "UZS"), Order("a8", 1, "XX1"), Order("a9", 1, "999"), Order("a10", "99999999999999999999", "UZS"),
        }));
        Assert.Equal(
        [
            ("accepted", null), ("accepted", null), ("accepted", null), ("accepted", null), ("accepted", null),
            ("rejected", "fractional_minor_units"), ("rejected", "invalid_amount"), ("rejected", "invalid_currency"), ("rejected", "invalid_currency"),
            ("rejected", "amount_out_of_range"),
        ], Items(r));
        var rows = await f.QueryAdminAsync(
            $"SELECT ref, amount_minor, currency FROM accounting_payment_orders WHERE tenant_id = '{org.TenantId}' ORDER BY ref",
            x => (x.GetString(0), x.GetInt64(1), x.GetString(2)));
        Assert.Equal([("a1", 123450L, "UZS"), ("a2", 10000L, "USD"), ("a3", 10L, "UZS"), ("a4", -525L, "UZS"), ("a5", 97800L, "EUR")], rows);
    }

    [Fact]
    public async Task Every_entity_is_stored_with_its_fields()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest all entities");
        var steps = new OneCSampleScenarios(f.Client(org));
        await steps.BasicAsync();
        Assert.All(steps.Steps, s => Assert.True(s.Ok, $"{s.Name}: expected {s.Expected}, got {s.Actual}"));

        Assert.Equal(5L, await f.CountAsync("accounting_counterparties", org.TenantId));
        Assert.Equal("UZS", await f.ValueAsync("accounting_contracts", org.TenantId, "ref = 'ct-001'", "currency"));
        Assert.Equal("30", await f.ValueAsync("accounting_contracts", org.TenantId, "ref = 'ct-001'", "payment_term_days"));
        Assert.Equal("2026-12-31", await f.ValueAsync("accounting_contracts", org.TenantId, "ref = 'ct-001'", "valid_until"));
        Assert.Equal("true", await f.ValueAsync("accounting_bank_accounts", org.TenantId, "ref = 'ba-001'", "is_active"));
        Assert.Equal("Sales", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'doc-001'", "doc_kind"));
        Assert.Equal("Purchase", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'doc-003'", "doc_kind"));
        Assert.Equal("100000000", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'doc-001'", "amount_total_minor"));
        Assert.Equal("10714286", await f.ValueAsync("accounting_documents", org.TenantId, "ref = 'doc-001'", "vat_amount_minor"));
        Assert.Equal("Payment", await f.ValueAsync("accounting_bank_documents", org.TenantId, "ref = 'bd-003'", "direction"));
        Assert.Equal("25000050", await f.ValueAsync("accounting_payment_orders", org.TenantId, "ref = 'po-001'", "amount_minor"));
        Assert.Equal(3L, await f.CountAsync("accounting_settlement_balances", org.TenantId));
        Assert.Equal("1234567890", await f.ValueAsync("accounting_bank_account_balances", org.TenantId, "bank_account_ref = 'ba-001'", "balance_minor"));
        // modified_at without an offset is Tashkent time (UTC+5).
        Assert.Equal("2026-11-01 04:00:00+00", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'cp-001'", "modified_at AT TIME ZONE 'UTC' || '+00'"));

        // A second run of the same data answers "unchanged" and rewrites nothing.
        var again = new OneCSampleScenarios(f.Client(org));
        await again.BasicAsync();
        Assert.All(again.Steps, s => Assert.Contains("accepted=0", s.Actual));
    }

    [Fact]
    public async Task The_messy_sample_scenario_behaves_as_expected()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest messy");
        var steps = new OneCSampleScenarios(f.Client(org));
        await steps.BasicAsync();
        await steps.MessyAsync();
        Assert.All(steps.Steps, s => Assert.True(s.Ok, $"{s.Name}: expected {s.Expected}, got {s.Actual}"));
        Assert.True(steps.Steps.Count >= 20);
    }
}
