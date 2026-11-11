using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synect.Application.Access;
using Synect.Application.Accounts;
using Synect.Application.Attention;
using Synect.Mcp.Responses;
using Synect.Mcp.Tools;

namespace Synect.Mcp.Hosting;

/// <summary>How a tool treats the 'organization' argument.</summary>
public enum OrganizationMode
{
    /// <summary>Works with exactly one organization (every analytics, reconciliation and refresh tool).</summary>
    Single,

    /// <summary>Like Single, but organization = "all" reads every available organization (attention list and summary).</summary>
    AllowsAll,

    /// <summary>Portfolio tools: every available organization by default, or the one named.</summary>
    Portfolio,

    /// <summary>update_attention_item: without 'organization' the organization of the item (item_id) is used.</summary>
    ItemLocator,
}

public sealed record OrganizationRefusal(string Code, string Message, IReadOnlyList<OrganizationInfo> Candidates);

/// <param name="Available">Every organization the key may work with.</param>
/// <param name="Single">The one organization of the call, or null.</param>
/// <param name="Many">The organizations of a multi-organization call, or null.</param>
public sealed record OrganizationResolution(
    IReadOnlyList<OrganizationInfo> Available, OrganizationInfo? Single, IReadOnlyList<OrganizationInfo>? Many, OrganizationRefusal? Error);

/// <summary>
/// The shared 'organization' mechanism of every tool (stage 6.5). The organizations come from the key only
/// (synect_api_key_organizations); the argument merely chooses among them by TIN (9 or 14 digits), id or name
/// (<see cref="OrganizationResolver"/>):
/// - a key with one organization needs no argument;
/// - a key with several and no argument gets organization_required with the list (name and TIN), never a guess;
/// - several similar names give organization_ambiguous with the candidates;
/// - anything else gives organization_not_found, worded the same whether the organization does not exist or belongs to
///   someone else (organizations outside the key's list are never looked at).
/// </summary>
public sealed class OrganizationScopeResolver(IApiKeyResolver keys, OrganizationFanOut fanOut, IOptions<AccountsOptions> options)
{
    public const string ArgumentName = "organization";

    /// <summary>Organizations listed in an error at most (the rest is counted).</summary>
    public const int MaxListed = 50;

    /// <summary>Values meaning "every available organization" (English, Russian, Uzbek Latin and Cyrillic).</summary>
    public static readonly IReadOnlySet<string> AllWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "all", "все", "всех", "все организации", "barchasi", "hammasi", "барчаси", "ҳаммаси",
    };

    public static OrganizationMode ModeOf(string tool) => tool switch
    {
        PortfolioTools.ListOrganizations or PortfolioTools.GetPortfolioOverview or PortfolioTools.GetConnectionsOverview => OrganizationMode.Portfolio,
        AttentionTools.GetItems or AttentionTools.GetSummary => OrganizationMode.AllowsAll,
        AttentionTools.UpdateItem => OrganizationMode.ItemLocator,
        _ => OrganizationMode.Single,
    };

    public async Task<OrganizationResolution> ResolveAsync(string tool, CallerIdentity id, IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var available = await keys.OrganizationsAsync(id.KeyHash, ct);
        if (available.Count == 0)
            return Refuse(available, ToolErrorCodes.Forbidden,
                "No organization is available to this API key (organizations may have been disabled). Ask the administrator.", []);

        string? value = null;
        if (args is not null && args.TryGetValue(ArgumentName, out var raw) && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (raw.ValueKind != JsonValueKind.String)
                return Refuse(available, ToolErrorCodes.InvalidArgument,
                    "Parameter 'organization' must be a string: the organization's TIN (9 or 14 digits), its name or its id.", []);
            value = raw.GetString()?.Trim();
            if (string.IsNullOrEmpty(value)) value = null;
        }

        var mode = ModeOf(tool);
        var all = value is not null && AllWords.Contains(value);
        if (mode == OrganizationMode.Portfolio && (value is null || all))
            return new OrganizationResolution(available, null, available, null);
        if (all)
            return mode == OrganizationMode.AllowsAll
                ? new OrganizationResolution(available, null, available, null)
                : Refuse(available, ToolErrorCodes.InvalidArgument,
                    "organization = \"all\" is accepted only by get_attention_items and get_attention_summary. For questions about all organizations call " +
                    "get_portfolio_overview (or list_organizations, get_connections_overview); otherwise name one organization.", []);

        if (value is null)
        {
            if (available.Count == 1) return One(available, available[0], mode);
            if (mode == OrganizationMode.ItemLocator && await LocateItemAsync(available, args, ct) is { } located) return located;
            return Refuse(available, ToolErrorCodes.OrganizationRequired,
                $"This API key serves {available.Count} organizations; the tool needs one. Ask the user which organization they mean and pass it in " +
                "'organization' (TIN, name or id from the list in 'organizations'). Never choose an organization yourself.", available);
        }

        var match = OrganizationResolver.Resolve(value, available, options.Value.MinNameSimilarity);
        return match.Kind switch
        {
            OrganizationMatchKind.Resolved => One(available, match.Organization!, mode),
            OrganizationMatchKind.Ambiguous => Refuse(available, ToolErrorCodes.OrganizationAmbiguous,
                $"{match.Candidates.Count} organizations available to this API key match '{Echo(value)}'. Show the candidates in 'organizations' " +
                "(name and TIN) to the user and ask which one they mean; then pass its TIN in 'organization'.", match.Candidates),
            _ => Refuse(available, ToolErrorCodes.OrganizationNotFound,
                $"No organization available to this API key matches '{Echo(value)}'. The available organizations are in 'organizations'; " +
                "ask the user to choose one of them (or call list_organizations).", available),
        };
    }

    private static OrganizationResolution One(IReadOnlyList<OrganizationInfo> available, OrganizationInfo organization, OrganizationMode mode) =>
        mode == OrganizationMode.Portfolio
            ? new OrganizationResolution(available, null, [organization], null)
            : new OrganizationResolution(available, organization, null, null);

    private static OrganizationResolution Refuse(IReadOnlyList<OrganizationInfo> available, string code, string message, IReadOnlyList<OrganizationInfo> candidates) =>
        new(available, null, null, new OrganizationRefusal(code, message, candidates));

    /// <summary>The argument as typed, cleaned and cut for the error message (it is the caller's own input).</summary>
    private static string Echo(string value) => TextSafety.Clean(value, 100, out _);

    /// <summary>
    /// update_attention_item without 'organization': looks for item_id in each available organization, each in its own
    /// scope; exactly one hit selects that organization. No hit or several hits fall back to organization_required.
    /// </summary>
    private async Task<OrganizationResolution?> LocateItemAsync(IReadOnlyList<OrganizationInfo> available, IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("item_id", out var raw) || raw.ValueKind != JsonValueKind.String
            || !Guid.TryParse(raw.GetString(), out var itemId))
            return null;
        var found = await fanOut.RunAsync(available.Select(o => o.TenantId).ToList(),
            (sp, _, token) => sp.GetRequiredService<AttentionService>().ExistsAsync(itemId, token), ct);
        var hits = found.Where(r => r is { Succeeded: true, Value: true }).Select(r => r.TenantId).ToList();
        return hits.Count == 1
            ? new OrganizationResolution(available, available.Single(o => o.TenantId == hits[0]), null, null)
            : null;
    }
}
