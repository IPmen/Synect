using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Ingest;
using Synect.Application.OData;
using Synect.Application.Secrets;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.OData;

namespace Synect.DevTools;

/// <summary>
/// 1C connections of either kind and the 1C:Fresh OData connector: create-connection --kind onec_fresh_odata|onec_extension,
/// disable-connection, sync-odata (with --dry-run), probe-odata. The OData password comes only from the environment variable
/// SYNECT_ODATA_PASSWORD (never an argument, never printed).
/// </summary>
public sealed class ODataCommands(IServiceProvider services)
{
    public const string PasswordVariable = "SYNECT_ODATA_PASSWORD";

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        return await work(scope.ServiceProvider);
    }

    private static SecretValue Password()
    {
        var value = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Set the OData password in the environment variable {PasswordVariable} (it is never an argument).");
        return new SecretValue(value);
    }

    private static string RequireUser(CliArgs options)
    {
        if (options.Get("password") is not null)
            throw new ArgumentException($"Do not pass the password as an argument; set {PasswordVariable}.");
        var user = options.Get("user");
        return string.IsNullOrWhiteSpace(user) || user == "true" ? throw new ArgumentException("--user <OData user> is required") : user;
    }

    /// <summary>create-connection --kind onec_fresh_odata --url ... --user ... [--profile ...] [--organization-key guid] [--name ...] [--test]</summary>
    public async Task<int> CreateODataAsync(Guid tenantId, CliArgs options)
    {
        var url = options.Get("url") ?? throw new ArgumentException("--url <application URL or .../odata/standard.odata> is required");
        var user = RequireUser(options);
        var password = Password();
        var test = options.Get("test") == "true";
        var r = await InTenantAsync(tenantId, sp => sp.GetRequiredService<ODataConnectionService>()
            .CreateAsync(options.Get("name"), url, user, password, options.Get("profile"), options.Get("organization-key"), test, CancellationToken.None));
        Console.WriteLine($"Connection created: {r.Name} (onec_fresh_odata, pull, host {r.HostLabel}, profile {r.Profile}), id {r.ConnectionId}");
        Console.WriteLine("  credentials stored encrypted; the password is not shown.");
        if (r.Test is { } t)
        {
            Console.WriteLine(t.Status == Synect.Application.Queue.ReactivationStatus.Reactivated
                ? $"  Test passed: Active, first load queued (job {t.JobId})."
                : $"  Test: {t.Status}{(t.Error is { } e ? $" ({e})" : "")}.");
            return t.Status == Synect.Application.Queue.ReactivationStatus.Reactivated ? 0 : 2;
        }
        Console.WriteLine("  Not tested: Active, loaded on the schedule (test-connection checks the access now; sync-odata --dry-run shows what would be loaded).");
        return 0;
    }

    /// <summary>create-connection --kind onec_extension: the push connection of the 1C extension (keys: create-ingest-key).</summary>
    public async Task<int> CreateExtensionAsync(Guid tenantId)
    {
        var r = await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None));
        Console.WriteLine(r.Created
            ? $"Connection created: {r.Name} (onec_extension, push, desktop), id {r.Id}. Create a key with create-ingest-key."
            : $"The organization already has the 1C extension connection {r.Name}, id {r.Id}.");
        return 0;
    }

    /// <summary>disable-connection --tenant X --connection id|name [--reason ...]</summary>
    public async Task<int> DisableAsync(Guid tenantId, CliArgs options)
    {
        var connection = options.Get("connection") ?? throw new ArgumentException("--connection <id|name> is required");
        var c = await InTenantAsync(tenantId, sp => sp.GetRequiredService<ODataConnectionService>().DisableAsync(connection, options.Get("reason"), CancellationToken.None));
        Console.WriteLine($"Connection {c.Name} ({c.Kind}) disabled; its data is kept, it is no longer loaded or accepted.");
        return 0;
    }

    /// <summary>sync-odata --tenant X --connection id|name [--mode auto|full|delta] [--dry-run]</summary>
    public async Task<int> SyncAsync(Guid tenantId, CliArgs options)
    {
        var key = options.Get("connection") ?? throw new ArgumentException("--connection <id|name> is required");
        var mode = options.Enum("mode", ODataSyncMode.Auto);
        var dryRun = options.Get("dry-run") == "true";
        var report = await InTenantAsync(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var id = Guid.TryParse(key, out var g)
                ? g
                : await db.InTransactionAsync(t => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                    db.Connections.Where(c => c.Name == key && c.SourceCode == Connection.OneCFreshODataSourceCode).Select(c => c.Id), t), CancellationToken.None);
            return await sp.GetRequiredService<ODataSyncService>().RunAsync(id, mode, dryRun, CancellationToken.None);
        });
        Console.WriteLine($"{(report.DryRun ? "DRY RUN (nothing written)" : "Loaded")}: mode {report.Mode}, profile {report.Profile}, {report.Requests} requests ({report.Retries} retries), {report.ElapsedMs} ms");
        Console.WriteLine($"{"entity",-16} {"mode",-6} {"listed",7} {"new",6} {"changed",8} {"restored",9} {"same",6} {"absent",7} {"send",6} {"pkgs",5} {"accepted",9} {"rejected",9} {"deleted",8}");
        foreach (var e in report.Entities)
        {
            Console.WriteLine($"{e.Entity,-16} {e.Mode,-6} {e.Listed,7} {e.New,6} {e.Changed,8} {e.Restored,9} {e.Unchanged,6} {e.MissingInSource,7} {e.Sent,6} {e.Packages,5} {e.Accepted,9} {e.Rejected,9} {e.MarkedDeleted,8}");
            foreach (var w in e.Warnings) Console.WriteLine($"    ! {w}");
        }
        foreach (var w in report.Warnings) Console.WriteLine($"! {w}");
        if (report.MissingProperties.Count > 0)
            Console.WriteLine("Properties of the profile absent from the records (check the profile): " + string.Join(", ", report.MissingProperties));
        if (report.DryRun) Console.WriteLine("'absent' records would be marked deleted when a full load completes.");
        return report.Complete ? 0 : 3;
    }

    /// <summary>
    /// probe-odata --url ... --user ... [--profile name] [--out path]: what the application publishes and how it answers, written
    /// to docs/odata-probe-&lt;date&gt;.md WITHOUT the host, values or TINs (property names, JSON types, counts, timings only).
    /// </summary>
    public async Task<int> ProbeAsync(CliArgs options)
    {
        var url = options.Get("url") ?? throw new ArgumentException("--url is required");
        var user = RequireUser(options);
        var password = Password();
        var odata = services.GetRequiredService<IOptions<ODataOptions>>().Value;
        var root = ODataEndpoint.Normalize(url, odata);
        var profile = ODataProfileResolver.ByName(options.Get("profile") ?? odata.DefaultProfile, odata);
        using var client = services.GetRequiredService<IODataClientFactory>().Create(root, user, password);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var md = new StringBuilder();
        md.AppendLine($"# OData probe {today:yyyy-MM-dd}");
        md.AppendLine();
        md.AppendLine($"- Host label: {ODataEndpoint.HostLabel(root)} (the host and the application number are not recorded)");
        md.AppendLine($"- Profile compared: {profile.Name}");
        md.AppendLine("- Only names, JSON types, counts and timings; no values, no TINs.");
        md.AppendLine();

        async Task<(T? Value, string Outcome, long Ms)> Step<T>(Func<Task<T>> work)
        {
            var w = Stopwatch.StartNew();
            try
            {
                var v = await work();
                return (v, "ok", w.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is AdapterException or ArgumentException or InvalidOperationException)
            {
                return (default, "FAILED: " + ex.Message, w.ElapsedMilliseconds);
            }
        }

        // 1. $metadata
        var (metadata, metaOutcome, metaMs) = await Step(() => client.GetMetadataAsync(CancellationToken.None));
        md.AppendLine("## $metadata");
        md.AppendLine();
        md.AppendLine($"- {metaOutcome}, {metaMs} ms{(metadata is null ? "" : $", {metadata.Length:N0} characters")}");
        var entitySets = new List<string>();
        var entityTypes = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        if (metadata is not null)
        {
            try
            {
                var doc = XDocument.Parse(metadata);
                entitySets = doc.Descendants().Where(e => e.Name.LocalName == "EntitySet").Select(e => (string?)e.Attribute("Name")).OfType<string>().ToList();
                foreach (var t in doc.Descendants().Where(e => e.Name.LocalName is "EntityType" or "ComplexType"))
                    entityTypes[(string?)t.Attribute("Name") ?? ""] = t.Elements().Where(p => p.Name.LocalName == "Property")
                        .Select(p => ((string?)p.Attribute("Name") ?? "", (string?)p.Attribute("Type") ?? "")).ToList();
            }
            catch (System.Xml.XmlException)
            {
                md.AppendLine("- the document is not valid XML");
            }
        }
        md.AppendLine();

        // 2. Entities
        md.AppendLine("## Published entity sets");
        md.AppendLine();
        md.AppendLine($"{entitySets.Count} entity sets.");
        md.AppendLine();
        var wanted = profile.Entities.SelectMany(e => e.Sources).Select(s => s.Resource.Name)
            .Concat(profile.Entities.SelectMany(e => e.Sources).SelectMany(s => s.Fields.Values.Concat(s.Children?.Tables.SelectMany(t => t.Fields.Values) ?? []))
                .Select(f => f.Lookup?.Resource).OfType<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        md.AppendLine("| Resource of the profile | Published |");
        md.AppendLine("|---|---|");
        foreach (var r in wanted) md.AppendLine($"| {r} | {(entitySets.Count == 0 ? "?" : entitySets.Contains(r) ? "yes" : "**no**")} |");
        md.AppendLine();
        if (entitySets.Count > 0)
        {
            md.AppendLine("<details><summary>All entity sets</summary>");
            md.AppendLine();
            foreach (var s in entitySets.Order(StringComparer.Ordinal)) md.AppendLine($"- {s}");
            md.AppendLine();
            md.AppendLine("</details>");
            md.AppendLine();
        }

        // 3. Each resource of the profile: count, first record's fields, filters, paging.
        foreach (var source in profile.Entities.SelectMany(e => e.Sources.Select(s => (e.Entity, Source: s))))
        {
            var resource = source.Source.Resource.Name;
            md.AppendLine($"## {resource} ({source.Entity})");
            md.AppendLine();
            var (count, countOutcome, countMs) = await Step(() => client.CountAsync(resource, null, CancellationToken.None));
            md.AppendLine($"- /$count: {countOutcome}{(countOutcome == "ok" ? $", {count}" : "")} ({countMs} ms)");
            var (first, firstOutcome, firstMs) = await Step(() => client.GetAsync(new ODataQuery(resource) { Top = 1 }, CancellationToken.None));
            md.AppendLine($"- first record ($top=1): {firstOutcome} ({firstMs} ms)");
            string? version = null;
            if (first is { Items.Count: > 0 })
            {
                var record = first.Items[0];
                version = record.TryGetProperty(profile.VersionProperty, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                md.AppendLine();
                md.AppendLine("| Property | JSON type | In profile |");
                md.AppendLine("|---|---|---|");
                var used = source.Source.Fields.Values.SelectMany(f => new[] { f.Source, f.Fallback, f.VatSource, f.HeaderFlag }).OfType<string>()
                    .Concat(source.Source.Children?.Tables.Select(t => t.Property.Name) ?? []).ToHashSet(StringComparer.Ordinal);
                foreach (var p in record.EnumerateObject())
                {
                    md.AppendLine($"| {p.Name} | {TypeOf(p.Value)} | {(used.Contains(p.Name) ? "yes" : "")} |");
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0 && p.Value[0].ValueKind == JsonValueKind.Object)
                        foreach (var q in p.Value[0].EnumerateObject()) md.AppendLine($"| {p.Name}/{q.Name} | {TypeOf(q.Value)} | |");
                }
                var missing = used.Where(u => !record.TryGetProperty(u, out _)).ToList();
                if (missing.Count > 0) md.AppendLine($"\nProfile properties absent from the record: **{string.Join(", ", missing)}**");
            }
            md.AppendLine();
            if (source.Source.DateProperty is { } dp)
            {
                var since = today.AddDays(-30);
                var (page, o, ms) = await Step(() => client.GetAsync(new ODataQuery(resource)
                {
                    Filter = $"{dp.Name} ge {ODataLiteral.DateTime(since)}", Select = [profile.KeyProperty], Top = 1, InlineCount = true,
                }, CancellationToken.None));
                md.AppendLine($"- $filter by {dp.Name} (last 30 days) with $inlinecount: {o}{(page?.Count is { } c ? $", odata.count {c}" : page is null ? "" : ", no odata.count")} ({ms} ms)");
            }
            if (version is not null)
            {
                var (page, o, ms) = await Step(() => client.GetAsync(new ODataQuery(resource)
                {
                    Filter = $"{profile.VersionProperty} eq {ODataLiteral.String(version)}", Select = [profile.KeyProperty], Top = 1,
                }, CancellationToken.None));
                md.AppendLine($"- $filter by {profile.VersionProperty}: {o}{(page is null ? "" : $", {page.Items.Count} record(s)")} ({ms} ms)");
            }
            var (p1, o1, ms1) = await Step(() => client.GetAsync(new ODataQuery(resource)
            { Select = [profile.KeyProperty, profile.VersionProperty], OrderBy = profile.KeyProperty, Top = 2, Skip = 0, InlineCount = true }, CancellationToken.None));
            var (p2, o2, ms2) = await Step(() => client.GetAsync(new ODataQuery(resource)
            { Select = [profile.KeyProperty, profile.VersionProperty], OrderBy = profile.KeyProperty, Top = 2, Skip = 2, InlineCount = true }, CancellationToken.None));
            var overlap = p1 is not null && p2 is not null
                ? p1.Items.Select(i => i.TryGetProperty(profile.KeyProperty, out var k) ? k.GetString() : null)
                    .Intersect(p2.Items.Select(i => i.TryGetProperty(profile.KeyProperty, out var k) ? k.GetString() : null)).Count()
                : -1;
            md.AppendLine($"- paging $top=2 $skip=0/2 $orderby={profile.KeyProperty}: page 1 {o1} ({p1?.Items.Count} rows, {ms1} ms), page 2 {o2} ({p2?.Items.Count} rows, {ms2} ms)" +
                          (overlap >= 0 ? $", overlap {overlap}" : "") + (p1?.Count is { } total ? $", odata.count {total} ({(p1.Count == p2?.Count ? "same on both pages" : "differs")})" : ""));
            md.AppendLine();
        }

        md.AppendLine("## Totals");
        md.AppendLine();
        md.AppendLine($"- {client.Stats.Requests} requests, {client.Stats.Retries} retries, {client.Stats.TotalMilliseconds} ms in requests");
        var outPath = options.Get("out") ?? Path.Combine("docs", $"odata-probe-{today:yyyy-MM-dd}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, md.ToString());
        Console.WriteLine($"Probe written to {outPath} (no host, values or TINs).");
        return metadata is null ? 2 : 0;
    }

    private static string TypeOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String when Guid.TryParse(e.GetString(), out _) => "string (guid)",
        JsonValueKind.String when DateTime.TryParseExact(e.GetString(), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "string (datetime)",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => e.ValueKind.ToString(),
    };
}
