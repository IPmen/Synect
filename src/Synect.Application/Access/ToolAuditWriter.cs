using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Access;

namespace Synect.Application.Access;

public sealed record ToolAuditEntry(
    Guid TenantId, Guid ApiKeyId, string Tool, string ParametersJson, DateTimeOffset StartedAt,
    TimeSpan Duration, int? RowCount, ToolCallStatus Status, string? ErrorMessage, string? DetailsJson = null, Guid? AccountId = null);

/// <summary>
/// Writes tool_audit_log in its own scope and transaction, so that the row exists even when the tool call
/// failed or was cancelled. A failure to write is logged (without arguments) and never replaces the answer.
/// </summary>
public sealed class ToolAuditWriter(TenantScopeRunner scopes, ILogger<ToolAuditWriter> logger)
{
    public const int MaxErrorLength = 2000;

    public async Task WriteAsync(ToolAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await scopes.InTenantAsync(entry.TenantId, async (sp, token) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                await db.InTransactionAsync(async t =>
                {
                    db.ToolAuditLogs.Add(new ToolAuditLog
                    {
                        ApiKeyId = entry.ApiKeyId,
                        AccountId = entry.AccountId,
                        Tool = entry.Tool.Length > 64 ? entry.Tool[..64] : entry.Tool,
                        Parameters = entry.ParametersJson,
                        StartedAt = entry.StartedAt,
                        DurationMs = (int)Math.Clamp(entry.Duration.TotalMilliseconds, 0, int.MaxValue),
                        RowCount = entry.RowCount,
                        Status = entry.Status,
                        ErrorMessage = entry.ErrorMessage is { Length: > MaxErrorLength } m ? m[..MaxErrorLength] : entry.ErrorMessage,
                        Details = entry.DetailsJson,
                    });
                    await db.SaveChangesAsync(t);
                }, token);
                return true;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write tool_audit_log for tool {Tool} (tenant {TenantId})", entry.Tool, entry.TenantId);
        }
    }
}
