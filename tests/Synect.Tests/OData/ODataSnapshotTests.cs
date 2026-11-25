using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Accounts;
using Synect.Application.OData;

namespace Synect.Tests.OData;

/// <summary>
/// Registers read as snapshots of their virtual tables (stock and settlement balances from Хозрасчетный/Balance, prices from
/// ЦеныНоменклатуры/SliceLast): accounts found by code, subconto in any order, rows grouped by the receiver's key, absent rows
/// removed by the next full snapshot, an empty source held back, optional entities skipped when not published.
/// </summary>
public sealed partial class ODataSyncTests
{
    private const string Balance = "AccountingRegister_Хозрасчетный/Balance";
    private const string Prices = "InformationRegister_ЦеныНоменклатуры/SliceLast";
    private const string Acc2910 = "e5555555-0000-0000-0000-000000002910";
    private const string Acc4010 = "e5555555-0000-0000-0000-000000004010";
    private const string Acc6010 = "e5555555-0000-0000-0000-000000006010";
    private const string Store1 = "c7000000-0000-0000-0000-000000000001";
    private const string Store2 = "c7000000-0000-0000-0000-000000000002";
    private const string Item2 = "d4444444-0000-0000-0000-000000000002";
    private const string Retail = "c8000000-0000-0000-0000-000000000001";
    private const string NomType = "StandardODATA.Catalog_Номенклатура";
    private const string StoreType = "StandardODATA.Catalog_Склады";

    private static JsonObject Row(string account, params (string Value, string Type)[] subconto)
    {
        var row = new JsonObject { ["Account_Key"] = account, ["Организация_Key"] = FakeODataServer.Org, ["Валюта_Key"] = FakeODataServer.ODataNull };
        for (var i = 0; i < 3; i++)
        {
            row[$"ExtDimension{i + 1}"] = i < subconto.Length ? subconto[i].Value : FakeODataServer.ODataNull;
            row[$"ExtDimension{i + 1}_Type"] = i < subconto.Length ? subconto[i].Type : "";
        }
        return row;
    }

    private static JsonObject Stock(string account, decimal quantity, params (string, string)[] subconto)
    {
        var r = Row(account, subconto);
        r["КоличествоBalance"] = quantity;
        r["СуммаBalance"] = quantity * 1000;
        return r;
    }

    private static void SeedRegisters(FakeODataServer s)
    {
        s.Add("ChartOfAccounts_Хозрасчетный", new JsonObject { ["Ref_Key"] = Acc2910, ["Code"] = "2910" });
        s.Add("ChartOfAccounts_Хозрасчетный", new JsonObject { ["Ref_Key"] = Acc4010, ["Code"] = "4010" });
        s.Add("ChartOfAccounts_Хозрасчетный", new JsonObject { ["Ref_Key"] = Acc6010, ["Code"] = "6010" });
        s.Add("Catalog_Склады", new JsonObject { ["Ref_Key"] = Store1, ["Description"] = "Основной склад", ["IsFolder"] = false });
        s.Add("Catalog_Склады", new JsonObject { ["Ref_Key"] = Store2, ["Description"] = "Магазин", ["IsFolder"] = false });
        s.Add("Catalog_ТипыЦенНоменклатуры", new JsonObject { ["Ref_Key"] = Retail, ["Description"] = "Розничная", ["ВалютаЦены_Key"] = FakeODataServer.Uzs });

        // Stock: item A in store 1 in two batches (summed); item 2 in store 2 with the subconto in another order; a row without a store.
        var batch = "StandardODATA.Document_ПоступлениеТоваровУслуг";
        s.AddRow(Balance, Stock(Acc2910, 5, (FakeODataServer.Item, NomType), (Store1, StoreType), (FakeODataServer.Purchase1, batch)));
        s.AddRow(Balance, Stock(Acc2910, 3, (FakeODataServer.Item, NomType), (Store1, StoreType), (Guid.NewGuid().ToString("D"), batch)));
        s.AddRow(Balance, Stock(Acc2910, 7.5m, (Store2, StoreType), (Item2, NomType)));
        s.AddRow(Balance, Stock(Acc2910, 1, (Item2, NomType)));

        // Settlements: a buyer with two settlement documents under one contract (summed), a supplier without a contract.
        var cp = "StandardODATA.Catalog_Контрагенты";
        var contract = "StandardODATA.Catalog_ДоговорыКонтрагентов";
        var doc = "StandardODATA.Document_РеализацияТоваровУслуг";
        var a = Row(Acc4010, (FakeODataServer.CpA, cp), (FakeODataServer.Contract, contract), (FakeODataServer.Sale1, doc));
        a["СуммаBalanceDr"] = 1_000_000m;
        a["СуммаBalanceCr"] = 0;
        s.AddRow(Balance, a);
        var b = Row(Acc4010, (FakeODataServer.CpA, cp), (FakeODataServer.Contract, contract), (Guid.NewGuid().ToString("D"), doc));
        b["СуммаBalanceDr"] = 120_000.5m;
        b["СуммаBalanceCr"] = 0;
        s.AddRow(Balance, b);
        // A supplier on a currency account: Сумма is in the accounting currency all the same, so it is stored as UZS.
        var c = Row(Acc6010, (FakeODataServer.CpB, cp));
        c["СуммаBalanceDr"] = 0;
        c["СуммаBalanceCr"] = 500m;
        c["Валюта_Key"] = FakeODataServer.Usd;
        c["ВалютнаяСуммаBalanceCr"] = 0.04m;
        s.AddRow(Balance, c);
        // An account the profile does not ask for: AccountCondition keeps it out.
        var bank = Row(FakeODataServer.Gl);
        bank["СуммаBalanceDr"] = 999m;
        bank["КоличествоBalance"] = 999m;
        s.AddRow(Balance, bank);

        s.AddRow(Prices, new JsonObject
        {
            ["Period"] = "2026-11-01T00:00:00", ["Номенклатура_Key"] = FakeODataServer.Item, ["ТипЦен_Key"] = Retail, ["Цена"] = 15000, ["Валюта_Key"] = FakeODataServer.Uzs,
        });
    }

    private Task<string?> StockOf(Guid tenant, string item, string store) =>
        f.ValueAsync($"SELECT quantity::text FROM catalog_stock WHERE tenant_id = '{tenant}' AND item_ref = '{item}' AND warehouse_ref = '{store}'");

    [Fact]
    public async Task Stock_settlements_and_prices_are_read_from_register_snapshots_and_grouped_by_the_receivers_key()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData registers");
        SeedRegisters(server);
        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);

        Assert.True(report.Complete);
        foreach (var e in new[] { "stock", "settlement_balances", "prices", "warehouses", "price_types" })
            Assert.Equal(("full", 0), (E(report, e).Mode, E(report, e).Rejected));
        Assert.Contains(E(report, "stock").Warnings, w => w.StartsWith("1 of 4 rows had no item_ref/warehouse_ref", StringComparison.Ordinal));

        Assert.Equal(2, await f.CountAsync("catalog_stock", tenant));
        Assert.Equal(8m, decimal.Parse((await StockOf(tenant, FakeODataServer.Item, Store1))!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(7.5m, decimal.Parse((await StockOf(tenant, Item2, Store2))!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, await f.CountAsync("catalog_warehouses", tenant));
        Assert.Equal("1500000", await f.ValueAsync($"SELECT price_minor FROM catalog_prices WHERE tenant_id = '{tenant}' AND item_ref = '{FakeODataServer.Item}'"));

        var balances = await f.QueryAsync($"""
            SELECT counterparty_ref, coalesce(contract_ref, ''), gl_account_code, currency, coalesce(balance_debit_minor, 0), coalesce(balance_credit_minor, 0), as_of_date
            FROM accounting_settlement_balances WHERE tenant_id = '{tenant}' AND NOT is_deleted ORDER BY gl_account_code
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), DateOnly.FromDateTime(r.GetDateTime(6))));
        Assert.Equal(
        [
            (FakeODataServer.CpA, FakeODataServer.Contract, "4010", "UZS", 112_000_050L, 0L, Today),
            (FakeODataServer.CpB, "", "6010", "UZS", 0L, 50_000L, Today),
        ], balances);

        // The URL carried the accounts (and not the bank account), the date and no organization condition (none is set).
        var parameters = server.VirtualTableParameters.Where(p => p.Resource == Balance).ToList();
        Assert.Contains(parameters, p => p.Name == "Period" && p.Literal == $"datetime'{Today:yyyy-MM-dd}T23:59:59'");
        Assert.Contains(parameters, p => p.Name == "AccountCondition" && p.Literal.Contains(Acc2910, StringComparison.Ordinal));
        Assert.DoesNotContain(parameters, p => p.Literal.Contains(FakeODataServer.Gl, StringComparison.Ordinal));
        Assert.DoesNotContain(parameters, p => p.Name == "Condition");
        Assert.Contains(server.Requests, r => r.Resource.StartsWith(Balance + "(", StringComparison.Ordinal) && r.Query.Contains("$skip=2", StringComparison.Ordinal));

        // The catalog is fresh as a data group of its own.
        Assert.Equal(1, await f.CountAsync("ingest_freshness", tenant, $"connection_id = '{connection}' AND data_group = 'Catalog'"));
    }

    [Fact]
    public async Task The_next_snapshot_removes_absent_rows_and_an_empty_source_is_held_back_until_confirmed()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData registers again");
        SeedRegisters(server);
        await SyncAsync(tenant, connection, ODataSyncMode.Full);

        // Item 2 sold out in store 2; one more unit of item A arrived in store 1.
        Later();
        var rows = server.Resources[Balance];
        rows.RemoveAll(r => (string?)r["ExtDimension1"] == Store2);
        rows.Add(Stock(Acc2910, 1, (FakeODataServer.Item, NomType), (Store1, StoreType)));
        var auto = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>().RunAsync(connection, ODataSyncMode.Auto, false, CancellationToken.None));
        Assert.Equal("full", E(auto, "stock").Mode); // a snapshot is full in Auto too
        Assert.Equal(1, await f.CountAsync("catalog_stock", tenant));
        Assert.Equal(9m, decimal.Parse((await StockOf(tenant, FakeODataServer.Item, Store1))!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, await f.CountAsync("ingest_removed_rows", tenant, "entity = 'stock'"));

        // The register comes back empty: nothing is removed, the load is incomplete.
        Later();
        rows.RemoveAll(r => (string?)r["Account_Key"] == Acc2910);
        var held = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.False(held.Complete);
        Assert.True(E(held, "stock").MassDeleteBlocked);
        Assert.Equal(1, await f.CountAsync("catalog_stock", tenant));

        // Confirmed by hand: the stock is emptied.
        Later();
        var confirmed = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>()
            .RunAsync(connection, ODataSyncMode.Full, false, CancellationToken.None, "tester@host"));
        Assert.True(E(confirmed, "stock").MassDeleteConfirmed);
        Assert.Equal(0, await f.CountAsync("catalog_stock", tenant));
    }

    [Fact]
    public async Task Unpublished_registers_are_skipped_without_making_the_load_incomplete_or_overdue()
    {
        RequirePostgres();
        var (tenant, connection, _) = await NewAsync("OData no registers");
        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);

        Assert.True(report.Complete);
        foreach (var e in new[] { "stock", "settlement_balances", "prices", "warehouses", "price_types" })
        {
            Assert.Equal("skipped", E(report, e).Mode);
            Assert.False(E(report, e).FullAttempted);
        }
        Assert.Contains(E(report, "stock").Warnings, w => w.Contains("none of the accounts", StringComparison.Ordinal));
        Assert.Contains(E(report, "prices").Warnings, w => w.Contains("HTTP 404", StringComparison.Ordinal));

        var states = (await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None))).Data!
            .Connections.Single(c => c.ConnectionId == connection).FullSyncs!;
        Assert.DoesNotContain(states, s => s.Entity is "stock" or "settlement_balances" or "prices" or "warehouses" or "price_types");
    }

    [Fact]
    public async Task The_organization_of_the_connection_goes_into_the_condition_of_the_virtual_table()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData registers organization");
        SeedRegisters(server);
        var foreign = Stock(Acc2910, 4, (Item2, NomType), (Store1, StoreType));
        foreign["Организация_Key"] = Guid.NewGuid().ToString("D");
        server.AddRow(Balance, foreign);
        await f.ExecAdminAsync($"UPDATE connections SET settings = settings || jsonb_build_object('organization_key', '{FakeODataServer.Org}') WHERE id = '{connection}'");

        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Contains(server.VirtualTableParameters, p => p.Resource == Balance && p.Name == "Condition" && p.Literal.Contains(FakeODataServer.Org, StringComparison.Ordinal));
        Assert.Null(await StockOf(tenant, Item2, Store1)); // the other organization's stock is not loaded
        Assert.Equal(2, await f.CountAsync("catalog_stock", tenant));
    }
}

/// <summary>Virtual-table URLs and the profile rules of snapshot entities (no database).</summary>
public sealed class ODataSnapshotProfileTests
{
    [Fact]
    public void A_virtual_table_url_carries_encoded_literal_parameters_only()
    {
        var q = new ODataQuery("AccountingRegister_Хозрасчетный")
        {
            VirtualTable = "Balance",
            Parameters =
            [
                new("Period", ODataLiteral.DateTime(new DateTime(2026, 11, 25, 23, 59, 59))),
                new("Condition", ODataLiteral.String("Организация_Key eq guid'c3333333-0000-0000-0000-000000000001'")),
            ],
            Top = 2,
        };
        var e = ODataUrl.Encode;
        Assert.Equal(
            e("AccountingRegister_Хозрасчетный") + "/Balance(Period=" + e("datetime'2026-11-25T23:59:59'") + ",Condition="
            + e("'Организация_Key eq guid''c3333333-0000-0000-0000-000000000001'''") + ")?$format=json&$top=2",
            ODataUrl.Build(q));
        Assert.EndsWith(")/$count", ODataUrl.Count(q));

        Assert.Throws<ArgumentException>(() => ODataUrl.Build(q with { Parameters = [new("Condition", "1 eq 1")] }));
        Assert.Throws<ArgumentException>(() => ODataUrl.Build(q with { VirtualTable = "Records" }));
        Assert.Throws<ArgumentException>(() => ODataUrl.Build(new ODataQuery("Catalog_Склады") { Parameters = [new("Period", "datetime'2026-11-25T00:00:00'")] }));
    }

    private static string Profile(string entity) => $$"""{ "name": "t", "entities": [ {{entity}} ] }""";

    [Theory]
    [InlineData("""{ "entity": "counterparties", "snapshot": true, "sources": [ { "resource": { "name": "Catalog_Контрагенты" }, "fields": { "name": { "source": "Description" } } } ] }""", "keyed by ref")]
    [InlineData("""{ "entity": "stock", "sources": [ { "resource": { "name": "R" }, "fields": { "quantity": { "source": "Q" } } } ] }""", "snapshot of a register")]
    [InlineData("""{ "entity": "stock", "snapshot": true, "sources": [ { "resource": { "name": "R" }, "virtual_table": { "name": "Balance" }, "parameters": { "Condition": { "value": "X eq {user}" } }, "fields": { "quantity": { "source": "Q" } } } ] }""", "unknown placeholder")]
    [InlineData("""{ "entity": "stock", "snapshot": true, "sources": [ { "resource": { "name": "R" }, "virtual_table": { "name": "Balance" }, "parameters": { "Period": { "type": "datetime", "value": "2026-01-01" } }, "fields": { "quantity": { "source": "Q" } } } ] }""", "{today_start}")]
    [InlineData("""{ "entity": "stock", "snapshot": true, "sources": [ { "resource": { "name": "R" }, "virtual_table": { "name": "Balance" }, "parameters": { "AccountCondition": { "value": "{accounts}" } }, "fields": { "quantity": { "source": "Q" } } } ] }""", "without accounts")]
    [InlineData("""{ "entity": "counterparties", "sources": [ { "resource": { "name": "Catalog_Контрагенты" }, "virtual_table": { "name": "Balance" }, "fields": { "name": { "source": "Description" } } } ] }""", "belong to a snapshot")]
    public void Snapshot_rules_of_the_profile_are_checked(string entity, string message)
    {
        var ex = Assert.Throws<ODataProfileException>(() => ODataProfile.Parse(Profile(entity), "test"));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_built_in_profile_reads_stock_and_settlements_as_optional_snapshots()
    {
        var profile = ODataProfile.Parse(ODataProfile.BuiltIn["clobus-bp-uz"], "built in");
        var stock = profile.Entities.Single(e => e.Entity == "stock");
        Assert.True(stock.Snapshot && stock.Optional);
        Assert.Equal("Balance", stock.Sources.Single().VirtualTable!.Name);
        Assert.True(profile.Entities.Single(e => e.Entity == "settlement_balances").Snapshot);
        Assert.Equal("first", profile.Entities.Single(e => e.Entity == "prices").Duplicates);
    }
}
