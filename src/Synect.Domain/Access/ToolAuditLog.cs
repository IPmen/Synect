using Synect.Domain.Common;

namespace Synect.Domain.Access;

public enum ToolCallStatus
{
    Ok,
    Error,
    RateLimited,
    Timeout,
}

/// <summary>One MCP tool call (tool_audit_log). Written for every call, including failed and rate-limited ones.</summary>
public class ToolAuditLog : TenantEntity
{
    public Guid ApiKeyId { get; set; }
    public string Tool { get; set; } = string.Empty;

    /// <summary>Arguments as JSON, without secrets; long values are truncated.</summary>
    public string Parameters { get; set; } = "{}";

    public DateTimeOffset StartedAt { get; set; }
    public int DurationMs { get; set; }
    public int? RowCount { get; set; }
    public ToolCallStatus Status { get; set; }

    /// <summary>Error message without secrets (never the API key).</summary>
    public string? ErrorMessage { get; set; }
}
