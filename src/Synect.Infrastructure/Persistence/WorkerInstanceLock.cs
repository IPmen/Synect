using Npgsql;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Guarantees a single running sync worker per database: a session-level PostgreSQL advisory lock held on a
/// dedicated, non-pooled connection for the whole life of the process. A second worker does not get the lock and
/// takes no jobs. The lock goes away with the session, so a killed process never leaves it behind. If the
/// connection is lost, the lock is lost too: <see cref="IsHeldAsync"/> reports it and the worker must stop.
/// Advisory locks need no privilege, so the application role (synect_app) can take it.
/// </summary>
public sealed class WorkerInstanceLock(string connectionString) : IAsyncDisposable
{
    /// <summary>Lock key: "SYNECTWK" as a 64-bit number. Advisory locks are per database.</summary>
    public const long LockKey = 0x53594E454354574B;

    private NpgsqlConnection? _connection;

    public bool Acquired { get; private set; }

    /// <summary>Tries to take the lock once (no waiting). True when this process holds it.</summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if (Acquired) return true;
        await CloseAsync();
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Never return this session to a pool: the lock lives exactly as long as this connection.
            Pooling = false,
            ApplicationName = "synect-worker-lock",
            // A dead peer is noticed within about a minute.
            KeepAlive = 30,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", LockKey);
            Acquired = (bool)(await command.ExecuteScalarAsync(ct))!;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        if (Acquired) _connection = connection;
        else await connection.DisposeAsync();
        return Acquired;
    }

    /// <summary>True while the session that holds the lock is alive.</summary>
    public async Task<bool> IsHeldAsync(CancellationToken ct)
    {
        if (!Acquired || _connection is null) return false;
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted " +
                "AND ((classid::bigint << 32) | objid::bigint) = @key", _connection);
            command.Parameters.AddWithValue("key", LockKey);
            var held = (long)(await command.ExecuteScalarAsync(ct))! > 0;
            if (!held) Acquired = false;
            return held;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Acquired = false;
            return false;
        }
    }

    private async Task CloseAsync()
    {
        if (_connection is null) return;
        try
        {
            if (Acquired && _connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                command.Parameters.AddWithValue("key", LockKey);
                await command.ExecuteScalarAsync();
            }
        }
        catch (NpgsqlException) { }
        finally
        {
            Acquired = false;
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
