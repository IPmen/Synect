using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Application.Attention;
using Synect.Application.Freshness;
using Synect.Domain.Attention;
using Synect.Mcp.Hosting;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

internal static class AttentionText
{
    public const string About =
        " Attention items are findings of Synect's own rules over the loaded bank and Didox data (thresholds in Attention:*, assumptions still to be" +
        " checked on real data); severity is critical, high, medium or low; status is new, seen, snoozed, dismissed, resolved (the" +
        " condition is no longer found) or archived (dismissed long ago and its condition has been gone since). Describe each item to the user as a POSSIBLE discrepancy with its basis (body and details), never as a" +
        " confirmed violation, and translate title and body into the user's language.";
}

/// <summary>The attention inbox: what the platform found after loads and reconciliation, and the accountant's marks on it.</summary>
[McpServerToolType]
public sealed class AttentionTools(
    ToolRunner runner, AttentionService attention, McpCaller caller, ISourceFreshnessQuery freshness, ITenantContext tenant,
    IOptions<AttentionOptions> options, TimeProvider time)
{
    public const string GetItems = "get_attention_items";
    public const string GetSummary = "get_attention_summary";
    public const string UpdateItem = "update_attention_item";

    /// <summary>Tools whose answers carry no attention block and no reminder.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { GetItems, GetSummary, UpdateItem };

    [McpServerTool(Name = GetItems, Title = "Items that need attention", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists what the platform found that needs the accountant's attention (the attention inbox): broken or stale connections, day balances that " +
                 "do not add up, possible double payments, payments to another account, large payments without a document, documents waiting for a signature " +
                 "or rejected. Sorted by severity (critical first), then the newest first. By default only new and seen items. Each item has id, rule_code, " +
                 "category, severity, status, title and body (English, translate them), details (identifiers and amounts; grouped items have count, totals and " +
                 "up to 20 entries) and entity. Examples: «Что требует внимания?», «Есть ли критичные проблемы?», «Покажи новые предупреждения по сверке», " +
                 "«Nimalarga e'tibor berish kerak?», «Нималарга эътибор бериш керак?»." + AttentionText.About + Common)]
    public Task<CallToolResult> GetAttentionItems(
        [Description("Optional severities, comma separated: critical, high, medium, low.")] string? severity = null,
        [Description("Optional categories, comma separated: connection, bank, reconciliation, documents.")] string? category = null,
        [Description("Optional statuses, comma separated: new, seen, snoozed, dismissed, resolved, archived (default: new,seen).")] string? status = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(GetItems, ToolGroup.Attention, async (a, ct) =>
        {
            var filter = new AttentionFilter(
                a.EnumList("severity", severity, ToolEnums.Severity),
                a.EnumList("category", category, ToolEnums.Category),
                a.EnumList("status", status, ToolEnums.AttentionStatus));
            var r = await attention.ListAsync(filter, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, Item);
        }, cancellationToken);

    [McpServerTool(Name = GetSummary, Title = "Attention summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Counts of the attention inbox: open (new and seen) items by severity and by category, how many are new, snoozed and dismissed, and when the " +
                 "rules last ran. Call it at the start of a conversation about the client's finances and tell the user briefly if there are new critical or " +
                 "high items. Examples: «Есть что-то срочное?», «Сколько новых предупреждений?», «Muhim ogohlantirishlar bormi?», «Муҳим огоҳлантиришлар борми?»." +
                 AttentionText.About + Common)]
    public Task<CallToolResult> GetAttentionSummary(CancellationToken cancellationToken = default) =>
        runner.RunAsync(GetSummary, ToolGroup.Attention, async (a, ct) =>
        {
            var r = await attention.SummaryAsync(ct);
            var s = r.Data;
            var urgent = s.NewBySeverity[AttentionSeverity.Critical] + s.NewBySeverity[AttentionSeverity.High];
            var notes = new List<string>();
            if (urgent > 0)
                notes.Add($"There are {urgent} new critical or high items; tell the user briefly and call get_attention_items with severity = critical,high for the details.");
            if (s.LastEvaluatedAt is null)
                notes.Add("No attention item has been found yet (the rules run after every load and reconciliation).");
            return new ToolOutput(new
            {
                s.Open,
                s.New,
                OpenBySeverity = s.OpenBySeverity,
                NewBySeverity = s.NewBySeverity,
                OpenByCategory = s.OpenByCategory,
                Breakdown = s.Breakdown.Select(b => new { b.Severity, b.Category, b.Open, b.New }).ToList(),
                s.Snoozed,
                s.Dismissed,
                s.LastEvaluatedAt,
            }, s.Breakdown.Count, r.Freshness, Notes: notes);
        }, cancellationToken);

    [McpServerTool(Name = UpdateItem, Title = "Mark an attention item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Marks an attention item (by item_id) for the accountant: seen (looked at), snooze (hide until a date or for a number of days; it comes back " +
                 "as new if the condition still holds), dismiss (needs nothing; a comment saying why is required; it stays dismissed until its significant " +
                 "content changes, e.g. the amount or new cases) or reopen (back to new). Resolved items cannot be changed: their condition is gone. Changes " +
                 "only Synect's attention list, never a source. Requires the review scope; call it only when the user explicitly asks. Examples: " +
                 "«Отметь как просмотренное», «Напомни об этом через неделю», «Скрой это, это наш второй счёт поставщика», «Ko'rib chiqildi deb belgila», " +
                 "«Бир ҳафтадан кейин эслат»." + Common)]
    public Task<CallToolResult> UpdateAttentionItem(
        [Description("item_id (the id of an item from get_attention_items).")] string item_id,
        [Description("Action: seen, snooze, dismiss or reopen.")] string action,
        [Description("snooze: local date (yyyy-MM-dd) when the item comes back; give this or days.")] string? until = null,
        [Description("snooze: number of days to hide the item (1-90); give this or until.")] int? days = null,
        [Description("Comment of the accountant (required for dismiss, at most 1000 characters).")] string? comment = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(UpdateItem, ToolGroup.Attention, async (a, ct) =>
        {
            var id = a.RequiredGuid("item_id", item_id);
            var what = a.Enum("action", action, ToolEnums.AttentionAction);
            var text = a.OptionalComment("comment", comment, AttentionItem.MaxCommentLength);
            DateTimeOffset? snoozeUntil = null;
            if (what == AttentionAction.Snooze)
            {
                var date = a.OptionalDate("until", until);
                if ((date is null) == (days is null))
                    throw new ToolArgumentException("For snooze give either 'until' (yyyy-MM-dd) or 'days' (1-90), not both.");
                var max = options.Value.MaxSnoozeDays;
                snoozeUntil = date is { } d
                    ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), runner.LocalOffset).ToUniversalTime()
                    : time.GetUtcNow().AddDays(a.Int("days", days, 1, 1, max));
            }
            else if (until is not null || days is not null)
                throw new ToolArgumentException("'until' and 'days' apply to snooze only.");
            if (what == AttentionAction.Dismiss && text is null)
                throw new ToolArgumentException("Dismiss needs a comment saying why the item needs nothing (ask the user).");

            var result = await attention.UpdateAsync(id, what, snoozeUntil, text, caller.ApiKeyId, ct);
            if (result.Outcome == AttentionUpdateOutcome.NotFound)
                throw new ToolArgumentException("No attention item with this item_id exists for the client. Take item_id from get_attention_items.");
            if (result.Outcome == AttentionUpdateOutcome.NotAllowed)
                throw new ToolArgumentException(result.Reason!);
            var changed = result.Outcome == AttentionUpdateOutcome.Changed;
            caller.AuditDetails = JsonSerializer.Serialize(new
            {
                item_id = id,
                action = what.ToString().ToLowerInvariant(),
                previous_status = result.PreviousStatus?.ToString(),
                new_status = result.Status?.ToString(),
                changed,
                snoozed_until = result.SnoozedUntil,
                event_id = result.EventId,
            });
            var sources = runner.FreshnessOf(await freshness.GetAsync(tenant.TenantId!.Value, ct));
            return new ToolOutput(new
            {
                ItemId = id,
                Result = result.Outcome,
                result.PreviousStatus,
                result.Status,
                result.SnoozedUntil,
            }, 1, sources, Notes:
            [
                changed ? $"The item is now {result.Status.ToString()!.ToLowerInvariant()}; the change is kept in the item's history."
                    : "The item already had this status; nothing changed.",
                "Only Synect's attention list changed; no source system (Didox, bank, 1C) was contacted.",
            ]);
        }, cancellationToken);

    private object Item(AttentionItemView i) => new
    {
        i.Id,
        i.RuleCode,
        i.Category,
        i.Severity,
        i.Status,
        i.Title,
        i.Body,
        Details = JsonDocument.Parse(i.Details).RootElement.Clone(),
        Entity = new { Kind = i.EntityKind, Ref = i.EntityRef },
        i.FirstSeenAt,
        i.LastSeenAt,
        i.SnoozedUntil,
        i.ResolvedAt,
        i.DecidedByKey,
        Comment = TextSafety.Untrusted(i.Comment, runner.Options.UntrustedTextMaxLength),
    };
}
