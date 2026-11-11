using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Abstractions;
using Microsoft.Extensions.Options;
using Synect.Application.Access;
using Synect.Application.Accounts;
using Synect.Domain.Access;
using Synect.Mcp.Responses;
using Synect.Mcp.Tools;

namespace Synect.Mcp.Hosting;

/// <summary>
/// Wraps every tools/call: resolves the organization the call works with (from the key and the optional 'organization'
/// argument, see <see cref="OrganizationScopeResolver"/>), sets the tenant context, checks the scope, applies the rate and
/// concurrency limits and writes the journal for every call (ok, error, rate_limited, timeout): tool_audit_log per
/// organization read (tenant_id = that organization, account_id of an account key), and account_tool_audit_log for calls
/// of an account key that did not resolve to exactly one organization.
/// </summary>
public static class ToolCallFilter
{
    /// <summary>Scope each tool needs; tools not listed need "read".</summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredScopes = new Dictionary<string, string>
    {
        [SourceTools.RequestDataRefresh] = ApiKey.ScopeSyncRequest,
        [ReviewTools.ConfirmMatch] = ApiKey.ScopeReview,
        [ReviewTools.RejectMatch] = ApiKey.ScopeReview,
        [AttentionTools.UpdateItem] = ApiKey.ScopeReview,
    };

    public static string RequiredScope(string tool) => RequiredScopes.GetValueOrDefault(tool, ApiKey.ScopeRead);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() => next => async (context, cancellationToken) =>
    {
        var services = context.Services ?? throw new InvalidOperationException("MCP request services are not available.");
        var runner = services.GetRequiredService<ToolRunner>();
        var tool = context.Params?.Name ?? "";
        var identity = SynectClaims.Identity(context.User);
        if (identity is not { } id)
            return runner.Error(tool, ToolGroup.None, null, ToolErrorCodes.Unauthenticated, "The request is not authenticated.");

        var caller = services.GetRequiredService<McpCaller>();
        caller.ApiKeyId = id.ApiKeyId;
        caller.AccountId = id.AccountId;

        var time = services.GetRequiredService<TimeProvider>();
        var started = time.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var parameters = AuditParameters.Serialize(context.Params?.Arguments);
        CallToolResult? result = null;
        Exception? failure = null;
        try
        {
            var scope = RequiredScope(tool);
            if (!SynectClaims.HasScope(context.User, scope))
            {
                result = runner.Error(tool, ToolGroup.None, null, ToolErrorCodes.Forbidden,
                    $"The API key does not have the '{scope}' scope required by this tool.");
                return result;
            }

            var resolution = await services.GetRequiredService<OrganizationScopeResolver>()
                .ResolveAsync(tool, id, context.Params?.Arguments, cancellationToken);
            caller.Available = resolution.Available;
            if (resolution.Error is { } refusal)
            {
                result = runner.Error(tool, ToolGroup.None, null, refusal.Code, refusal.Message, organizations: refusal.Candidates);
                return result;
            }
            if (resolution.Single is { } organization)
            {
                // The tenant comes from the key's organization list only. Every service of this call reads it.
                caller.Organization = organization;
                services.GetRequiredService<TenantContext>().Set(organization.TenantId);
            }
            else
            {
                // Several organizations: the tool reads each in its own scope (OrganizationFanOut); this scope has no tenant.
                caller.Organizations = resolution.Many;
            }

            if (services.GetRequiredService<ToolCallGate>().TryEnter(id.ApiKeyId, caller.Organization?.TenantId, id.AccountId, out var rejection) is { } lease)
            {
                using (lease) result = await next(context, cancellationToken);
            }
            else
            {
                result = runner.Error(tool, ToolGroup.None, null, ToolErrorCodes.RateLimited, rejection!.Message,
                    Math.Ceiling(rejection.RetryAfter.TotalSeconds));
            }
            return result;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var (status, rows, error) = Outcome(result, failure, cancellationToken);
            await WriteAuditAsync(services, id, caller, tool, parameters, started, stopwatch.Elapsed, rows, status, error);
        }
    };

    /// <summary>
    /// One tool_audit_log row per organization the call worked with; for a single-organization key whose call was refused
    /// before that, the key's own organization. An account key call that did not resolve to one organization also gets
    /// one account_tool_audit_log row (with the organizations it read, if any).
    /// </summary>
    private static async Task WriteAuditAsync(IServiceProvider services, CallerIdentity id, McpCaller caller, string tool, string parameters,
        DateTimeOffset started, TimeSpan elapsed, int? rows, ToolCallStatus status, string? error)
    {
        var writer = services.GetRequiredService<ToolAuditWriter>();
        IReadOnlyList<Guid> tenants = caller.Organization is { } single ? [single.TenantId]
            : caller.Organizations is { } many ? many.Select(o => o.TenantId).ToList()
            : id.TenantId is { } own ? [own]
            : [];
        var details = caller.AuditDetails;
        if (caller.Organizations is { } multi)
            details = JsonSerializer.Serialize(new { multi_organization = true, organizations = multi.Count, tool_details = caller.AuditDetails });
        var perOrganizationRows = tenants.Count == 1 ? rows : null;
        var parallel = Math.Max(1, services.GetRequiredService<IOptions<AccountsOptions>>().Value.MaxParallelOrganizations);
        await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = parallel },
            async (tenant, _) => await writer.WriteAsync(
                new ToolAuditEntry(tenant, id.ApiKeyId, tool, parameters, started, elapsed, perOrganizationRows, status, error, details, id.AccountId),
                CancellationToken.None));

        if (id.AccountId is not null && caller.Organization is null)
            await services.GetRequiredService<IAccountToolAuditWriter>().WriteAsync(id.KeyHash,
                new AccountToolAuditEntry(tool, parameters, started, elapsed, rows, status, error, caller.AuditDetails, tenants),
                CancellationToken.None);
    }

    private static (ToolCallStatus Status, int? Rows, string? Error) Outcome(CallToolResult? result, Exception? failure, CancellationToken ct)
    {
        if (failure is not null)
            return failure is OperationCanceledException && ct.IsCancellationRequested
                ? (ToolCallStatus.Error, null, "The call was cancelled by the client.")
                : (ToolCallStatus.Error, null, failure is ModelContextProtocol.McpException m ? m.Message : "Unhandled error (" + failure.GetType().Name + ").");
        if (result?.StructuredContent is not { ValueKind: JsonValueKind.Object } body)
            return (result?.IsError == true ? ToolCallStatus.Error : ToolCallStatus.Ok, null, null);

        int? rows = body.TryGetProperty("returned_rows", out var r) && r.TryGetInt32(out var n) ? n : null;
        if (result.IsError != true || !body.TryGetProperty("error", out var error)) return (ToolCallStatus.Ok, rows, null);

        var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
        var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : null;
        var status = code switch
        {
            ToolErrorCodes.RateLimited => ToolCallStatus.RateLimited,
            ToolErrorCodes.Timeout => ToolCallStatus.Timeout,
            _ => ToolCallStatus.Error,
        };
        return (status, rows, $"{code}: {message}");
    }
}

/// <summary>
/// The caller of the current tool call (set by <see cref="ToolCallFilter"/>): the API key (e.g. to record who decided a
/// match), its account, and the organization(s) the call works with.
/// </summary>
public sealed class McpCaller
{
    public Guid? ApiKeyId { get; set; }

    /// <summary>The account of an account key; null for a single-organization key.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Every organization the key may work with (ordered by name).</summary>
    public IReadOnlyList<OrganizationInfo> Available { get; set; } = [];

    /// <summary>The one organization of the call; its tenant is the tenant context of this scope.</summary>
    public OrganizationInfo? Organization { get; set; }

    /// <summary>The organizations of a multi-organization call (portfolio tools, organization = "all"); no tenant context.</summary>
    public IReadOnlyList<OrganizationInfo>? Organizations { get; set; }

    public bool IsMultiOrganization => Organizations is not null;

    /// <summary>Set by a changing tool: what it did, as JSON, written to tool_audit_log.details.</summary>
    public string? AuditDetails { get; set; }
}

/// <summary>Tool arguments for tool_audit_log: JSON, long strings cut, secret-looking names redacted.</summary>
public static class AuditParameters
{
    public const int MaxStringLength = 200;
    public const int MaxProperties = 50;
    private static readonly string[] SecretNames = ["key", "token", "secret", "password", "authorization", "credential"];

    public static string Serialize(IDictionary<string, JsonElement>? arguments)
    {
        var result = new JsonObject();
        if (arguments is null) return result.ToJsonString();
        foreach (var (name, value) in arguments.Take(MaxProperties))
        {
            var safeName = name.Length > 64 ? name[..64] : name;
            result[safeName] = SecretNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))
                ? JsonValue.Create("[redacted]")
                : Clean(value, depth: 0);
        }
        return result.ToJsonString();
    }

    private static JsonNode? Clean(JsonElement value, int depth) => value.ValueKind switch
    {
        JsonValueKind.String => JsonValue.Create(Cut(value.GetString()!)),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => JsonNode.Parse(value.GetRawText()),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ when depth >= 2 => JsonValue.Create("[nested value omitted]"),
        JsonValueKind.Array => new JsonArray(value.EnumerateArray().Take(20).Select(e => Clean(e, depth + 1)).ToArray()),
        JsonValueKind.Object => new JsonObject(value.EnumerateObject().Take(20)
            .Select(p => KeyValuePair.Create(p.Name, Clean(p.Value, depth + 1)))),
        _ => null,
    };

    private static string Cut(string s) => s.Length > MaxStringLength ? s[..MaxStringLength] + "…" : s;
}
