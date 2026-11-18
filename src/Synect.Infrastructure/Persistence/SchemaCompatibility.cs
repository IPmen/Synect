using Microsoft.EntityFrameworkCore;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Schema state as seen by this build. <see cref="Compatible"/>: every migration this build knows is applied.
/// Migrations applied in the database but unknown to this build (<see cref="Unknown"/>, a newer release ran them)
/// are allowed: migrations are written backward compatible (expand first, contract in a later release), which is
/// what lets a rollback of the code run on the newer schema.
/// </summary>
public sealed record SchemaStatus(
    bool DatabaseAvailable,
    bool Compatible,
    string? ExpectedLatest,
    string? AppliedLatest,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unknown,
    string? Error);

/// <summary>Compares the migrations of this build with __ef_migrations_history (read only; never applies anything).</summary>
public sealed class SchemaCompatibility(SynectDbContext db)
{
    public async Task<SchemaStatus> CheckAsync(CancellationToken ct)
    {
        var expected = db.Database.GetMigrations().ToList();
        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var pending = expected.Except(applied, StringComparer.Ordinal).ToList();
            var unknown = applied.Except(expected, StringComparer.Ordinal).ToList();
            return new SchemaStatus(true, pending.Count == 0, expected.LastOrDefault(), applied.LastOrDefault(), pending, unknown, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the error type: a message may carry host names or user names of the connection.
            return new SchemaStatus(false, false, expected.LastOrDefault(), null, [], [], ex.GetType().Name);
        }
    }
}
