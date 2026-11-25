using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Access;
using Synect.Application.Accounts;
using Synect.Application.Analytics;
using Synect.Application.Ingest;
using Synect.Domain.Attention;
using Synect.Domain.Sync;
using Synect.Mcp.Hosting;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

internal static class PortfolioText
{
    public const string About =
        " Each organization is read separately in its own isolated context; figures of different organizations are never added together." +
        " Every row carries its organization (name and TIN): always name the organization when you report a figure.";
}

/// <summary>
/// Tools over every organization an API key serves (an accounting company with many clients). They read only ready
/// figures (connection states, attention_items, reconciliation results, document statuses), each organization in its own
/// scope and transaction through <see cref="OrganizationFanOut"/>; nothing is calculated across organizations in SQL.
/// </summary>
[McpServerToolType]
public sealed class PortfolioTools(ToolRunner runner, McpCaller caller, OrganizationFanOut fanOut, IOptions<AccountsOptions> accounts,
    TimeProvider time, ILogger<PortfolioTools> logger)
{
    public const string ListOrganizations = "list_organizations";
    public const string GetPortfolioOverview = "get_portfolio_overview";
    public const string GetConnectionsOverview = "get_connections_overview";

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
        { ListOrganizations, GetPortfolioOverview, GetConnectionsOverview };

    public const int DefaultOrganizations = 25;
    public const int MaxOrganizations = 100;

    private const string ReadFailed = "The figures of this organization could not be read now (timeout or error); ask about it alone with another tool and organization = its TIN.";

    [McpServerTool(Name = ListOrganizations, Title = "Organizations of the key", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the organizations (client companies) this API key serves: name, TIN (ИНН / STIR), number of connected sources (bank, Didox) and " +
                 "their states, and the number of NEW attention items by severity. Use it when the user asks which clients are connected, or to find the " +
                 "TIN to pass as 'organization' to other tools. Examples: «Какие у нас клиенты?», «Покажи список организаций», «ИНН ООО Ромашка?», " +
                 "«Qaysi tashkilotlar ulangan?», «Mijozlar ro'yxati», «Қайси ташкилотлар уланган?», «Мижозлар рўйхати»." + PortfolioText.About + Common)]
    public Task<CallToolResult> ListOrganizationsAsync(CancellationToken cancellationToken = default) =>
        runner.RunAsync(ListOrganizations, ToolGroup.None, async (a, ct) =>
        {
            var orgs = caller.Organizations ?? [];
            var summaries = await SummariesAsync(orgs, withWorkload: false, ct);
            var rows = summaries.Select(s => (object)new
            {
                Organization = runner.Organization(s.Organization),
                Connections = s.Summary is { } x ? ConnectionCounts(x.Connections) : null,
                NewAttention = s.Summary is { } y ? Severities(y.NewBySeverity) : null,
                ReadError = s.Summary is null ? ReadFailed : null,
            }).ToList();
            return new ToolOutput(new { Organizations = rows, Total = orgs.Count }, rows.Count, null, Notes: CommonNotes(summaries).ToList());
        }, cancellationToken);

    [McpServerTool(Name = GetPortfolioOverview, Title = "Portfolio overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Overview of ALL organizations (clients) of the API key, or of a list of them: per organization the state and freshness of every source " +
                 "(bank, Didox), NEW attention items by severity, the number of unmatched payments (поступления и списания without a matching ЭСФ / акт in the " +
                 "reconciliation, recent days) and the number of documents waiting for OUR signature (ожидают нашей подписи). Sorted by importance: organizations " +
                 "with critical items or broken connections first. Use it for questions about all clients at once. Paged by organizations (default 25, max 100). " +
                 "Examples: «Что по всем клиентам?», «У каких клиентов проблемы?», «Где есть критичные предупреждения?», «Сколько несопоставленных платежей по " +
                 "всем организациям?», «Barcha mijozlar bo'yicha holat qanday?», «Qaysi mijozlarda muammo bor?», «Барча мижозлар бўйича ҳолат қандай?», " +
                 "«Қайси мижозларда муаммо бор?»." + PortfolioText.About + Common)]
    public Task<CallToolResult> GetPortfolioOverviewAsync(
        [Description("Optional list of organizations, comma separated (TIN, name or id each), e.g. «301234567, Ромашка». Default: all organizations of the key.")] string? organizations = null,
        [Description("Organizations per answer: default 25, max 100.")] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(GetPortfolioOverview, ToolGroup.Attention | ToolGroup.Reconciliation, async (a, ct) =>
        {
            var orgs = Select(a, organizations);
            var rows = a.Int("limit", limit, DefaultOrganizations, 1, MaxOrganizations);
            var offset = Offset(a, cursor);
            var summaries = await SummariesAsync(orgs, withWorkload: true, ct);
            var now = time.GetUtcNow();
            var ordered = summaries.OrderBy(s => s.Summary is null ? 0 : 1) // unknown state first: it may hide a problem
                .ThenByDescending(s => Importance(s.Summary))
                .ThenBy(s => s.Organization.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Organization.TenantId)
                .ToList();
            var page = ordered.Skip(offset).Take(rows).ToList();
            var next = offset + page.Count < ordered.Count ? Cursor.Encode(offset + page.Count) : null;
            var days = accounts.Value.UnmatchedLookbackDays;
            var data = page.Select(s => (object)new
            {
                Organization = runner.Organization(s.Organization),
                TopSeverity = s.Summary is { } t ? TopSeverity(t) : null,
                NewAttention = s.Summary is { } x ? Severities(x.NewBySeverity) : null,
                OpenCritical = s.Summary?.OpenCritical,
                Sources = s.Summary?.Connections.Select(c => Source(c, now)).ToList(),
                UnmatchedPayments = s.Summary is { UnmatchedPayments: { } u } ? new { Count = u, LastDays = days } : null,
                DocumentsAwaitingOurSignature = s.Summary?.AwaitingOurSignature,
                ReadError = s.Summary is null ? ReadFailed : null,
            }).ToList();
            var notes = CommonNotes(summaries).ToList();
            notes.Add($"Unmatched payments: error-free payments with a counterparty INN in the reconciliation currency of the last {days} days " +
                      "that are in no proposed or confirmed match (Accounts:UnmatchedLookbackDays); details per organization: get_unmatched_payments with organization = its TIN.");
            notes.Add("Sorted by importance: new critical items or a broken connection first, then new high items, then the other counts. Details: get_attention_items with organization = the TIN.");
            return new ToolOutput(new { Organizations = data, Total = ordered.Count }, data.Count, null, next, Notes: notes);
        }, cancellationToken);

    [McpServerTool(Name = GetConnectionsOverview, Title = "Connections of all organizations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("State of every source (bank, Didox, statement file import, 1C — for 1C the last load is its last accepted package) of every organization of the API key: status (active, needs_attention, disabled), " +
                 "reason, connection_kind (onec_extension, onec_fresh_odata, didox, kapital_bank, file_import), transport (push: the source sends data; pull: Synect loads it), " +
                 "host_label of 1C (desktop, clobus, other), time of the last successful load and of the last attempt, whether data is stale, whether credentials are set " +
                 "(never their value) and the ingest key prefix, whether the last load was complete, for 1C:Fresh (OData) per entity the time of the last " +
                 "completed full export (last_full_sync_at) and of the last attempt with its failure, and the manual onboarding mark (onboarding_status: not_started, " +
                 "waiting_for_access, connected, needs_attention, with a note such as «заявление в банк подано»). only_needing_attention = true keeps only sources " +
                 "that are not active, were never loaded or are marked as needing attention. Examples: «Какие подключения не работают?», «У кого не загружается " +
                 "банк?», «Где ждём доступ от банка?», «Qaysi ulanishlar ishlamayapti?», «Bank ko'chirmasi kimda yuklanmayapti?», «Қайси уланишлар ишламаяпти?»." +
                 PortfolioText.About + Common)]
    public Task<CallToolResult> GetConnectionsOverviewAsync(
        [Description("true: only sources that need attention (not active, never loaded, or onboarding marked needs_attention). Default false.")] bool? only_needing_attention = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(GetConnectionsOverview, ToolGroup.None, async (a, ct) =>
        {
            var orgs = caller.Organizations ?? [];
            var onlyProblems = only_needing_attention == true;
            var page = a.Page(limit, cursor);
            var offset = Offset(a, cursor);
            var summaries = await SummariesAsync(orgs, withWorkload: false, ct);
            var now = time.GetUtcNow();
            var rows = summaries
                .SelectMany(s => s.Summary is { } x
                    ? x.Connections.Where(c => !onlyProblems || c.NeedsAttention).Select(c => (s.Organization, Connection: (OrganizationConnection?)c))
                    : [(s.Organization, Connection: (OrganizationConnection?)null)])
                .OrderBy(r => r.Connection is null ? 0 : r.Connection.NeedsAttention ? 1 : 2)
                .ThenBy(r => r.Organization.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.Organization.TenantId)
                .ThenBy(r => r.Connection?.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var take = page.Limit ?? runner.Options.DefaultLimit;
            var slice = rows.Skip(offset).Take(take).ToList();
            var next = offset + slice.Count < rows.Count ? Cursor.Encode(offset + slice.Count) : null;
            var data = slice.Select(r => (object)new
            {
                Organization = runner.Organization(r.Organization),
                Source = r.Connection is { } c ? Source(c, now) : null,
                ReadError = r.Connection is null ? ReadFailed : null,
            }).ToList();
            return new ToolOutput(new
            {
                Connections = data,
                Total = rows.Count,
                OnlyNeedingAttention = onlyProblems,
            }, data.Count, null, next, Notes: CommonNotes(summaries).Append(
                "onboarding_status and onboarding_note are manual marks of the onboarding steps; they do not change how a source is loaded.").ToList());
        }, cancellationToken);

    // ---- shared ----------------------------------------------------------------------------------------------------

    private sealed record Row(OrganizationInfo Organization, OrganizationSummary? Summary);

    /// <summary>One <see cref="PortfolioQueries.SummaryAsync"/> per organization, each in its own scope (never mixed).</summary>
    private async Task<IReadOnlyList<Row>> SummariesAsync(IReadOnlyList<OrganizationInfo> orgs, bool withWorkload, CancellationToken ct)
    {
        var results = await fanOut.RunAsync(orgs.Select(o => o.TenantId).ToList(), async (sp, _, token) =>
            (await sp.GetRequiredService<PortfolioQueries>().SummaryAsync(withWorkload, token)).Data, ct);
        var byTenant = orgs.ToDictionary(o => o.TenantId);
        foreach (var failed in results.Where(r => !r.Succeeded))
            logger.LogWarning("Portfolio figures of tenant {TenantId} could not be read: {ErrorType}", failed.TenantId, failed.Error!.GetType().Name);
        return results.Select(r => new Row(byTenant[r.TenantId], r.Succeeded ? r.Value : null)).ToList();
    }

    private IReadOnlyList<OrganizationInfo> Select(ToolArgs a, string? list)
    {
        var orgs = caller.Organizations ?? [];
        if (string.IsNullOrWhiteSpace(list)) return orgs;
        var parts = list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > MaxOrganizations) throw new ToolArgumentException($"At most {MaxOrganizations} organizations in 'organizations'.");
        var chosen = new List<OrganizationInfo>();
        foreach (var part in parts)
        {
            var match = OrganizationResolver.Resolve(part, orgs, accounts.Value.MinNameSimilarity);
            var shown = TextSafety.Clean(part, 100, out _);
            if (match.Kind == OrganizationMatchKind.Ambiguous)
                throw new ToolArgumentException($"'{shown}' matches {match.Candidates.Count} organizations (" +
                    string.Join("; ", match.Candidates.Take(10).Select(c => $"{TextSafety.Clean(c.Name, 80, out _)} TIN {c.Tin ?? "none"}")) +
                    "). Ask the user which one they mean and pass its TIN.");
            if (match.Kind == OrganizationMatchKind.NotFound)
                throw new ToolArgumentException($"No organization available to this API key matches '{shown}'. Call list_organizations to see the available organizations.");
            if (!chosen.Contains(match.Organization!)) chosen.Add(match.Organization!);
        }
        return chosen;
    }

    private static int Offset(ToolArgs a, string? cursor)
    {
        if (cursor is null) return 0;
        var parts = Cursor.Decode(cursor, 1);
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : throw new ToolArgumentException("Parameter 'cursor' must be the 'next_cursor' value of a previous answer of the same tool.");
    }

    /// <summary>Critical items or a broken connection first, then high, then the other counts (higher is more important).</summary>
    private static long Importance(OrganizationSummary? s)
    {
        if (s is null) return long.MaxValue;
        var broken = s.Connections.Count(c => c.Status == ConnectionStatus.NeedsAttention);
        var critical = s.NewBySeverity[AttentionSeverity.Critical] + broken;
        return Math.Min(critical, 999) * 1_000_000_000L
               + Math.Min(s.NewBySeverity[AttentionSeverity.High], 999) * 1_000_000L
               + Math.Min(s.NewBySeverity[AttentionSeverity.Medium], 999) * 1_000L
               + Math.Min(s.NewBySeverity[AttentionSeverity.Low], 999);
    }

    private static string? TopSeverity(OrganizationSummary s)
    {
        if (s.NewBySeverity[AttentionSeverity.Critical] > 0 || s.Connections.Any(c => c.Status == ConnectionStatus.NeedsAttention)) return "critical";
        foreach (var severity in new[] { AttentionSeverity.High, AttentionSeverity.Medium, AttentionSeverity.Low })
            if (s.NewBySeverity[severity] > 0) return severity.ToString().ToLowerInvariant();
        return null;
    }

    private static object Severities(IReadOnlyDictionary<AttentionSeverity, int> counts) => new
    {
        Critical = counts[AttentionSeverity.Critical],
        High = counts[AttentionSeverity.High],
        Medium = counts[AttentionSeverity.Medium],
        Low = counts[AttentionSeverity.Low],
        Total = counts.Values.Sum(),
    };

    private static object ConnectionCounts(IReadOnlyList<OrganizationConnection> connections) => new
    {
        Total = connections.Count,
        Active = connections.Count(c => c.Status == ConnectionStatus.Active),
        NeedsAttention = connections.Count(c => c.Status == ConnectionStatus.NeedsAttention),
        Disabled = connections.Count(c => c.Status == ConnectionStatus.Disabled),
        NeverLoaded = connections.Count(c => c.LastSuccessfulSyncAt is null),
        Sources = connections.Select(c => new { c.ConnectionId, Source = c.SourceCode, c.Kind, c.Status }).ToList(),
    };

    private object Source(OrganizationConnection c, DateTimeOffset now) => new
    {
        c.ConnectionId,
        ConnectionName = TextSafety.Untrusted(c.Name, runner.Options.UntrustedTextMaxLength),
        Source = c.SourceCode,
        c.Kind,
        c.ConnectionKind,
        c.Transport,
        c.HostLabel,
        ConnectionStatus = c.Status,
        StatusReason = TextSafety.Untrusted(c.StatusReason, runner.Options.UntrustedTextMaxLength),
        c.StatusChangedAt,
        LastSuccessfulLoadAt = c.LastSuccessfulSyncAt,
        LastSuccessAt = c.LastSuccessfulSyncAt,
        c.LastAttemptAt,
        // Secrets are never shown: only whether credentials are stored and the prefix of the ingest key.
        CredentialsSet = c.Transport == ConnectionKinds.TransportPull ? c.CredentialsSet : (bool?)null,
        c.IngestKeyPrefix,
        // 1C:Fresh over OData: per entity, when the last full export completed (deletions are marked only then) and the last attempt.
        FullSyncs = c.FullSyncs?.Select(s => new
        {
            s.Entity,
            s.LastFullSyncAt,
            s.LastAttemptAt,
            s.LastOutcome,
            LastFailure = s.LastAttemptFailed ? TextSafety.Untrusted(s.LastReason, runner.Options.UntrustedTextMaxLength) : null,
        }).ToList(),
        HoursSinceLastLoad = c.LastSuccessfulSyncAt is { } at ? Math.Round((now - at).TotalHours, 1) : (double?)null,
        Stale = c.DataGroups is { } groups
            ? IngestFreshnessGroups.IsStale(groups)
            : c.LastSuccessfulSyncAt is not { } last || now - last > (c.StaleAfter ?? runner.Options.StaleAfter),
        StaleAfterHours = (c.StaleAfter ?? runner.Options.StaleAfter).TotalHours,
        DataGroups = c.DataGroups is { } g ? runner.DataGroups(g, now) : null,
        LastLoadComplete = c.LastLoadComplete,
        c.OnboardingStatus,
        OnboardingNote = TextSafety.Untrusted(c.OnboardingNote, runner.Options.UntrustedTextMaxLength),
        c.NeedsAttention,
    };

    private IEnumerable<string> CommonNotes(IReadOnlyList<Row> rows)
    {
        yield return $"{rows.Count} organizations; each was read separately (its own isolated context), figures of different organizations are never added together.";
        var failed = rows.Count(r => r.Summary is null);
        if (failed > 0) yield return $"{failed} organizations could not be read now (read_error); their state is unknown, not good.";
        yield return $"Freshness is per source in each row (last_successful_load_at, stale = older than stale_after_hours: {runner.Options.StaleAfter.TotalHours:0} hours for bank and Didox; for 1C the time of the last accepted package, with data_groups: accounting stale after Ingest:AccountingStaleAfter, catalog after Ingest:CatalogStaleAfter, a group never received does not count unless nothing was ever received; or never loaded).";
    }
}
