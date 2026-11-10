using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Application.Reconciliation;
using Synect.Domain.Attention;

namespace Synect.Application.Attention;

/// <summary>What one evaluation changed in the item list.</summary>
public enum AttentionChangeKind
{
    Created,
    Reopened,
    Resolved,
}

public sealed record AttentionChange(Guid ItemId, string RuleCode, AttentionSeverity Severity, AttentionChangeKind Kind, string Title);

/// <param name="Open">Items of the evaluated rules that are open after the run (New, Seen, Snoozed).</param>
/// <param name="Findings">Cases found per rule code (before grouping).</param>
public sealed record AttentionEvaluation(
    IReadOnlyList<AttentionCategory> Categories, int Created, int Reopened, int Updated, int Resolved, int Open,
    IReadOnlyDictionary<string, int> Findings, IReadOnlyList<AttentionChange> Changes, TimeSpan Elapsed);

/// <summary>
/// Evaluates the attention rules of the current tenant (tenant from the context, RLS) over the Synect database only, and
/// keeps attention_items in step, idempotently:
/// - a case is identified by its dedup key (rule code plus the significant parameters) and is never created twice;
/// - an open item whose condition is gone becomes Resolved (event AutoResolved); a Resolved item whose condition comes
///   back is opened again as New (event Reopened) on the same row;
/// - a Snoozed item whose time is over becomes New again (event Reopened) if the condition still holds;
/// - a Dismissed item stays Dismissed while its content is unchanged (a changed amount is a different key, so a new item);
///   a dismissed grouped item opens again only when a case appears that was not in it when it was dismissed.
/// Only the rules of the requested categories run, so loads re-check only what their data can change.
/// Evaluations and person decisions of one tenant are serialized with a PostgreSQL advisory lock.
/// </summary>
public sealed class AttentionEvaluator(
    ISynectDbContext db,
    ITenantContext tenant,
    IOptions<AttentionOptions> options,
    IOptions<ReconciliationOptions> reconciliation,
    IOptions<AnalyticsOptions> analytics,
    TimeProvider time)
{
    public static readonly IReadOnlyList<AttentionCategory> AllCategories =
        [AttentionCategory.Connection, AttentionCategory.Bank, AttentionCategory.Reconciliation, AttentionCategory.Documents];

    public static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Rule codes per category (for documentation and tests).</summary>
    public static IReadOnlyDictionary<AttentionCategory, IReadOnlyList<string>> RuleCodes { get; } =
        AttentionRuleSet.All.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Code).ToList());

    public static AttentionSeverity SeverityOf(string ruleCode) => AttentionRuleSet.All.Single(r => r.Code == ruleCode).Severity;

    public Task<AttentionEvaluation> EvaluateAsync(IEnumerable<AttentionCategory> categories, CancellationToken ct)
    {
        var set = categories.Distinct().Order().ToList();
        return db.InTransactionAsync(token => RunAsync(set, token), ct);
    }

    /// <summary>Serializes attention writes of one tenant (no-op outside PostgreSQL).</summary>
    internal static async Task LockTenantAsync(ISynectDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        var key = "synect.attention:" + tenantId.ToString("N");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private async Task<AttentionEvaluation> RunAsync(List<AttentionCategory> categories, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Attention evaluation requires a tenant context.");
        var stopwatch = Stopwatch.StartNew();
        await LockTenantAsync(db, tenantId, ct);

        var now = time.GetUtcNow();
        var a = analytics.Value;
        var context = new RuleContext(db, tenantId, now, DateOnly.FromDateTime(now.ToOffset(a.UtcOffset).DateTime), options.Value,
            reconciliation.Value, a);
        var rules = AttentionRuleSet.All.Where(r => categories.Contains(r.Category)).ToList();
        var codes = rules.Select(r => r.Code).ToList();

        var candidates = new List<Candidate>();
        var findings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var found = (await rule.Find(context, ct)).DistinctBy(f => f.Key).ToList();
            findings[rule.Code] = found.Count;
            candidates.AddRange(Build(context, rule, found));
        }

        var open = await db.AttentionItems.Where(i => codes.Contains(i.RuleCode) && i.Status != AttentionStatus.Resolved).ToListAsync(ct);
        var openByKey = open.ToDictionary(i => i.DedupKey, StringComparer.Ordinal);
        var missingKeys = candidates.Select(c => c.Key).Where(k => !openByKey.ContainsKey(k)).ToList();
        var resolvedByKey = new Dictionary<string, AttentionItem>(StringComparer.Ordinal);
        foreach (var chunk in missingKeys.Chunk(1000))
        {
            var rows = await db.AttentionItems.Where(i => i.Status == AttentionStatus.Resolved && chunk.Contains(i.DedupKey)).ToListAsync(ct);
            foreach (var row in rows.OrderByDescending(r => r.ResolvedAt).ThenBy(r => r.Id))
                resolvedByKey.TryAdd(row.DedupKey, row);
        }

        var changes = new List<AttentionChange>();
        int created = 0, reopened = 0, updated = 0, resolved = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            seenKeys.Add(c.Key);
            if (openByKey.TryGetValue(c.Key, out var item))
            {
                var wasDismissed = item.Status == AttentionStatus.Dismissed;
                var newMembers = wasDismissed && c.Members.Except(item.MemberKeys, StringComparer.Ordinal).Any();
                if (Apply(item, c, keepMembers: wasDismissed && !newMembers, now)) updated++;
                item.LastSeenAt = now;
                if (item.EffectiveStatus(now) == AttentionStatus.New && item.Status == AttentionStatus.Snoozed)
                {
                    Reopen(item, now, "The snooze ended and the condition still holds.");
                    reopened++;
                    changes.Add(new(item.Id, item.RuleCode, item.Severity, AttentionChangeKind.Reopened, item.Title));
                }
                else if (newMembers)
                {
                    item.MemberKeys = c.Members;
                    Reopen(item, now, "New cases appeared since the item was dismissed.");
                    reopened++;
                    changes.Add(new(item.Id, item.RuleCode, item.Severity, AttentionChangeKind.Reopened, item.Title));
                }
                continue;
            }

            if (resolvedByKey.TryGetValue(c.Key, out item))
            {
                Apply(item, c, keepMembers: false, now);
                item.LastSeenAt = now;
                item.ResolvedAt = null;
                Reopen(item, now, "The condition is back after it was resolved.");
                reopened++;
                changes.Add(new(item.Id, item.RuleCode, item.Severity, AttentionChangeKind.Reopened, item.Title));
                continue;
            }

            item = new AttentionItem
            {
                RuleCode = c.Rule.Code,
                Category = c.Rule.Category,
                Severity = c.Rule.Severity,
                DedupKey = c.Key,
                Status = AttentionStatus.New,
                FirstSeenAt = now,
                LastSeenAt = now,
                UpdatedAt = now,
            };
            Apply(item, c, keepMembers: false, now);
            db.AttentionItems.Add(item);
            db.AttentionItemEvents.Add(new AttentionItemEvent { ItemId = item.Id, Kind = AttentionEventKind.Created, At = now });
            created++;
            changes.Add(new(item.Id, item.RuleCode, item.Severity, AttentionChangeKind.Created, item.Title));
        }

        foreach (var item in open.Where(i => !seenKeys.Contains(i.DedupKey)))
        {
            // Dismissed stays dismissed: the accountant decided about it; it is not resolved and not reopened by the platform.
            if (item.Status == AttentionStatus.Dismissed) continue;
            item.Status = AttentionStatus.Resolved;
            item.ResolvedAt = now;
            item.SnoozedUntil = null;
            item.UpdatedAt = now;
            db.AttentionItemEvents.Add(new AttentionItemEvent
            {
                ItemId = item.Id, Kind = AttentionEventKind.AutoResolved, At = now, Comment = "The condition is no longer found.",
            });
            resolved++;
            changes.Add(new(item.Id, item.RuleCode, item.Severity, AttentionChangeKind.Resolved, item.Title));
        }

        await db.SaveChangesAsync(ct);
        var openCount = await db.AttentionItems.CountAsync(i => codes.Contains(i.RuleCode)
            && (i.Status == AttentionStatus.New || i.Status == AttentionStatus.Seen || i.Status == AttentionStatus.Snoozed), ct);
        return new AttentionEvaluation(categories, created, reopened, updated, resolved, openCount, findings, changes, stopwatch.Elapsed);
    }

    private void Reopen(AttentionItem item, DateTimeOffset now, string comment)
    {
        item.Status = AttentionStatus.New;
        item.SnoozedUntil = null;
        item.UpdatedAt = now;
        db.AttentionItemEvents.Add(new AttentionItemEvent { ItemId = item.Id, Kind = AttentionEventKind.Reopened, At = now, Comment = comment });
    }

    /// <summary>Copies the content; returns whether anything visible changed.</summary>
    private static bool Apply(AttentionItem item, Candidate c, bool keepMembers, DateTimeOffset now)
    {
        var changed = item.Title != c.Title || item.Body != c.Body || item.Details != c.Details || item.EntityKind != c.EntityKind
                      || item.EntityRef != c.EntityRef || (!keepMembers && !item.MemberKeys.SequenceEqual(c.Members, StringComparer.Ordinal));
        if (!changed) return false;
        item.Title = c.Title;
        item.Body = c.Body;
        item.Details = c.Details;
        item.EntityKind = c.EntityKind;
        item.EntityRef = c.EntityRef;
        if (!keepMembers) item.MemberKeys = c.Members;
        item.UpdatedAt = now;
        return true;
    }

    private sealed record Candidate(AttentionRule Rule, string Key, string EntityKind, string EntityRef, string Title, string Body, string Details, string[] Members);

    /// <summary>One item per case, or one grouped item for list rules and for more than Attention:GroupAbove cases.</summary>
    private static IEnumerable<Candidate> Build(RuleContext context, AttentionRule rule, List<Finding> found)
    {
        if (found.Count == 0) yield break;
        var o = context.Options;
        if (!rule.Grouped && found.Count <= o.GroupAbove)
        {
            foreach (var f in found)
            {
                var (title, body) = rule.Single(context, f);
                var details = new Dictionary<string, object?>(f.Entry);
                if (rule.Note is not null) details["note"] = rule.Note;
                yield return new Candidate(rule, Key(rule.Code, f.Key), f.EntityKind, f.EntityRef, Cut(title, AttentionItem.MaxTitleLength),
                    Cut(body, AttentionItem.MaxBodyLength), JsonSerializer.Serialize(details, DetailsJson), (f.Members ?? []).ToArray());
            }
            yield break;
        }

        var (groupTitle, groupBody) = rule.Group(context, found);
        var listed = found.Take(o.MaxListedEntries).Select(f => f.Entry).ToList();
        var grouped = new Dictionary<string, object?>
        {
            ["count"] = found.Count,
            ["listed"] = listed.Count,
        };
        var totals = found
            .Where(f => f.Entry.GetValueOrDefault("amount_minor") is long && f.Entry.GetValueOrDefault("currency") is string)
            .GroupBy(f => (string)f.Entry["currency"]!)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Dictionary<string, object?> { ["currency"] = g.Key, ["count"] = g.Count(), ["amount_minor"] = g.Sum(f => (long)f.Entry["amount_minor"]!) })
            .ToList();
        if (totals.Count > 0) grouped["totals"] = totals;
        grouped["items"] = listed;
        if (rule.Note is not null) grouped["note"] = rule.Note;
        var members = found.SelectMany(f => f.Members is { Count: > 0 } m ? m.Select(x => f.Key + "/" + x) : [f.Key])
            .Order(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrEmpty(groupBody)) groupBody = "Details list the cases (identifiers and amounts).";
        yield return new Candidate(rule, Key(rule.Code, "all"), "tenant", "", Cut(groupTitle, AttentionItem.MaxTitleLength),
            Cut(groupBody, AttentionItem.MaxBodyLength), JsonSerializer.Serialize(grouped, DetailsJson), members);
    }

    private static string Key(string code, string key)
    {
        var full = code + ":" + key;
        if (full.Length <= 256) return full;
        return code + ":h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
