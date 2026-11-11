using Synect.Domain.Common;

namespace Synect.Domain.Access;

public enum ToolCallStatus
{
    Ok,
    Error,
    RateLimited,
    Timeout,
}

/// <summary>One MCP tool call (tool_audit_log). Written for every call, including failed and rate-limited ones;</summary>
/// <remarks>tenant_id is the organization whose data the call read or changed. Calls of an account key that resolved no single organization are
/// written to account_tool_audit_log instead (see synect_write_account_tool_audit).</remarks>
public class ToolAuditLog : TenantEntity
{
    public Guid ApiKeyId { get; set; }

    /// <summary>The account of an account key (stage 6.5); null for a single-organization key.</summary>
    public Guid? AccountId { get; set; }
    public string Tool { get; set; } = string.Empty;

    /// <summary>Arguments as JSON, without secrets; long values are truncated.</summary>
    public string Parameters { get; set; } = "{}";

    public DateTimeOffset StartedAt { get; set; }
    public int DurationMs { get; set; }
    public int? RowCount { get; set; }
    public ToolCallStatus Status { get; set; }

    /// <summary>Error message without secrets (never the API key).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>What a changing tool did, as JSON (e.g. confirm_match: previous and new status of the match); null for reads.</summary>
    public string? Details { get; set; }
}
