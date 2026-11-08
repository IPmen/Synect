using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>Runs one load of a connection with the right service. Called by the queue, not by API code.</summary>
public sealed class ConnectionSyncService(
    ISynectDbContext db,
    BankSyncService bankSync,
    EdoSyncService edoSync)
{
    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await db.InTransactionAsync(token =>
            db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, token), ct)
            ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");

        return connection.AdapterKind switch
        {
            AdapterKind.Bank => await bankSync.SyncAsync(connection.Id, ct),
            AdapterKind.Edo => await edoSync.SyncAsync(connection.Id, ct),
            _ => throw new NotSupportedException($"Adapter kind {connection.AdapterKind} is not supported yet."),
        };
    }
}
