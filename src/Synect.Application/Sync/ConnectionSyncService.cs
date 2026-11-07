using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

public sealed class BankSyncOptions
{
    /// <summary>Days to load on the first sync of a connection (capped by the adapter history depth).</summary>
    public int InitialDays { get; set; } = 30;

    /// <summary>Days before the last successful sync that are re-read to pick up late changes and day closing.</summary>
    public int RefetchDays { get; set; } = 3;

    /// <summary>UTC offset of the bank operational day. Uzbekistan is UTC+5 without DST.</summary>
    public TimeSpan OperationalDayUtcOffset { get; set; } = TimeSpan.FromHours(5);
}

/// <summary>Entry point for "sync this connection now": picks the right service and date range.</summary>
public sealed class ConnectionSyncService(
    ISynectDbContext db,
    IAdapterRegistry registry,
    BankSyncService bankSync,
    EdoSyncService edoSync,
    TimeProvider time,
    IOptions<BankSyncOptions> bankOptions)
{
    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await db.InTransactionAsync(token =>
            db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, token), ct)
            ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");

        switch (connection.AdapterKind)
        {
            case AdapterKind.Bank:
                var (from, to) = BankRange(connection, registry.GetBank(connection.SourceCode).Capabilities);
                return await bankSync.SyncAsync(connection.Id, from, to, ct);
            case AdapterKind.Edo:
                return await edoSync.SyncAsync(connection.Id, ct);
            default:
                throw new NotSupportedException($"Adapter kind {connection.AdapterKind} is not supported yet.");
        }
    }

    private (DateOnly From, DateOnly To) BankRange(Connection connection, BankAdapterCapabilities capabilities)
    {
        var options = bankOptions.Value;
        var offset = options.OperationalDayUtcOffset;
        var today = DateOnly.FromDateTime(time.GetUtcNow().ToOffset(offset).DateTime);

        var initialDays = capabilities.HistoryDepthDays is { } depth ? Math.Min(depth, options.InitialDays) : options.InitialDays;
        var from = connection.LastSuccessfulSyncAt is { } last
            ? DateOnly.FromDateTime(last.ToOffset(offset).DateTime).AddDays(-options.RefetchDays)
            : today.AddDays(-initialDays + 1);
        return (from > today ? today : from, today);
    }
}
