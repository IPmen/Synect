using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

public sealed record SyncOutcome(Guid SyncRunId, SyncRunResult Result, bool IsComplete, int RecordCount, string? Error);

internal static class SyncRunFinalizer
{
    /// <summary>
    /// Records a failed run. Pending (unsaved) changes of the failed step are discarded;
    /// steps that were already committed stay, which is safe because every step is idempotent.
    /// </summary>
    public static async Task<SyncOutcome> FailAsync(
        ISynectDbContext db, Guid runId, Guid connectionId, Exception error, int recordCount, DateTimeOffset now)
    {
        db.ChangeTracker.Clear();
        var needsAttention = error is AdapterAuthenticationException;
        var message = error switch
        {
            AdapterAuthenticationException => "Authentication failed: " + error.Message,
            OperationCanceledException => "Cancelled",
            _ => error.Message,
        };

        return await db.InTransactionAsync(async ct =>
        {
            var run = await db.SyncRuns.SingleAsync(r => r.Id == runId, ct);
            run.Fail(now, message, recordCount);
            if (needsAttention)
            {
                var connection = await db.Connections.SingleAsync(c => c.Id == connectionId, ct);
                connection.MarkNeedsAttention(message);
            }
            await db.SaveChangesAsync(ct);
            return new SyncOutcome(run.Id, run.Result, false, recordCount, run.Error);
        }, CancellationToken.None);
    }

    public static AdapterContext CreateAdapterContext(Connection connection)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(connection.Settings) ? "{}" : connection.Settings);
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
                settings[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        }
        return new AdapterContext(connection.TenantId, connection.Id, connection.SourceCode, settings, connection.CredentialsRef);
    }
}
