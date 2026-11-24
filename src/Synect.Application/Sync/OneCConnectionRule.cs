using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>A second 1C connection that is not Disabled was refused: the source of accounting records must be one.</summary>
public sealed class OneCConnectionConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// An organization has at most one 1C connection (onec_extension or onec_fresh_odata) that is not Disabled; a partial
/// unique index (ux_connections_one_active_1c) enforces the same in the database. Changing the source means disabling the
/// old connection explicitly first. Didox, banks and file imports are not affected.
/// </summary>
public static class OneCConnectionRule
{
    /// <summary>Throws <see cref="OneCConnectionConflictException"/> when another 1C connection of the current organization is not Disabled.</summary>
    public static async Task EnsureNoOtherActiveAsync(ISynectDbContext db, Guid? exceptConnectionId, CancellationToken ct)
    {
        var other = await db.Connections.AsNoTracking()
            .Where(c => c.AdapterKind == AdapterKind.Accounting1C && c.Status != ConnectionStatus.Disabled
                        && (exceptConnectionId == null || c.Id != exceptConnectionId))
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (other is not null)
            throw new OneCConnectionConflictException(
                $"The organization already has an active 1C connection '{other.Name}' ({other.Kind}, id {other.Id}). " +
                "Only one 1C source can be active: disable it first (disable-connection), then create the new one.");
    }
}
