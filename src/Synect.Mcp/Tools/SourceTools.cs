using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Abstractions;
using Synect.Application.Freshness;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

/// <summary>Data freshness and the only non-read-only tool: queueing a refresh.</summary>
[McpServerToolType]
public sealed class SourceTools(ToolRunner runner, ISourceFreshnessQuery freshness, ITenantContext tenant, SyncRequestService requests)
{
    public const string RequestDataRefresh = "request_data_refresh";

    [McpServerTool(Name = "get_data_freshness", Title = "Data freshness", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("For every connected source (bank, Didox): time of the last successful load, connection state and whether that load was complete. " +
                 "Use it before answering about recent days, or when the user asks how up to date the data is. " +
                 "Examples: «Насколько свежие данные?», «Когда последний раз загружалась выписка?», «Работает ли подключение к Didox?»." + Common)]
    public Task<CallToolResult> GetDataFreshness(CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_data_freshness", ToolGroup.None, async (a, ct) =>
        {
            var sources = await freshness.GetAsync(TenantId, ct);
            var f = runner.FreshnessOf(sources);
            return new ToolOutput(new { SourceCount = sources.Count }, sources.Count, f);
        }, cancellationToken);

    [McpServerTool(Name = RequestDataRefresh, Title = "Request a data refresh", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Queues a refresh (new load) of the client's bank and/or Didox data and returns at once; it does NOT wait for the load and does not " +
                 "contact the sources itself. Per connection the result is queued, already_queued, already_running, too_soon (with retry_after_seconds) " +
                 "or connection_not_active. Requires the sync_request scope. Check get_data_freshness later to see the new data. " +
                 "Examples: «Обнови данные банка», «Загрузи свежие документы из Didox», «Обнови всё»." + Common)]
    public Task<CallToolResult> RequestRefresh(
        [Description("Which sources to refresh: all (default), bank or edo.")] string source = "all",
        [Description("Optional connection_id (from data_freshness) to refresh one connection only.")] string? connection_id = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(RequestDataRefresh, ToolGroup.None, async (a, ct) =>
        {
            var which = a.Enum("source", source, ToolEnums.Source);
            var connection = a.OptionalGuid("connection_id", connection_id);
            var sources = await freshness.GetAsync(TenantId, ct);
            var selected = sources.Where(s => connection is { } id ? s.ConnectionId == id : which switch
            {
                "bank" => s.Kind == AdapterKind.Bank,
                "edo" => s.Kind == AdapterKind.Edo,
                _ => true,
            }).ToList();
            if (connection is not null && selected.Count == 0)
                throw new ToolArgumentException("No connection with this connection_id exists for the client. Take connection_id from data_freshness.");

            var results = new List<object>();
            foreach (var s in selected)
            {
                // Only queues a job (sync_jobs); the worker performs the load later.
                var r = await requests.RequestSyncAsync(s.ConnectionId, ct);
                results.Add(new
                {
                    s.ConnectionId,
                    Source = s.SourceCode,
                    s.Kind,
                    Result = r.Status,
                    r.JobId,
                    RetryAfterSeconds = r.RetryAfter is { } ra ? Math.Ceiling(ra.TotalSeconds) : (double?)null,
                    Reason = TextSafety.Untrusted(r.Reason, runner.Options.UntrustedTextMaxLength),
                });
            }
            var after = runner.FreshnessOf(await freshness.GetAsync(TenantId, ct));
            return new ToolOutput(new { Requests = results }, results.Count, after, Notes:
            [
                "The refresh only queues a load; it usually finishes within minutes. Answers keep using the previous data until then (see data_freshness).",
            ]);
        }, cancellationToken);

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Tenant context is not set.");
}
