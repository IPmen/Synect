using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Domain.Access;
using Synect.Mcp.Responses;
using Synect.Mcp.Tools;

namespace Synect.Mcp.Hosting;

/// <summary>
/// Wraps every tools/call: sets the tenant context from the authenticated key, checks the scope, applies the
/// rate and concurrency limits and writes tool_audit_log for every call (ok, error, rate_limited, timeout).
/// </summary>
public static class ToolCallFilter
{
    /// <summary>Scope each tool needs; tools not listed need "read".</summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredScopes = new Dictionary<string, string>
    {
        [SourceTools.RequestDataRefresh] = ApiKey.ScopeSyncRequest,
        [ReviewTools.ConfirmMatch] = ApiKey.ScopeReview,
        [ReviewTools.RejectMatch] = ApiKey.ScopeReview,
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

        // The tenant comes from the key only. Every service of this call (analytics, freshness, queue) reads it.
        services.GetRequiredService<TenantContext>().Set(id.TenantId);
        services.GetRequiredService<McpCaller>().ApiKeyId = id.ApiKeyId;

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
            }
            else if (services.GetRequiredService<ToolCallGate>().TryEnter(id.ApiKeyId, id.TenantId, out var rejection) is { } lease)
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
            await services.GetRequiredService<ToolAuditWriter>().WriteAsync(
                new ToolAuditEntry(id.TenantId, id.ApiKeyId, tool, parameters, started, stopwatch.Elapsed, rows, status, error,
                    services.GetRequiredService<McpCaller>().AuditDetails),
                CancellationToken.None);
        }
    };

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

/// <summary>The API key of the current tool call (set by <see cref="ToolCallFilter"/>), e.g. to record who decided a match.</summary>
public sealed class McpCaller
{
    public Guid? ApiKeyId { get; set; }

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
