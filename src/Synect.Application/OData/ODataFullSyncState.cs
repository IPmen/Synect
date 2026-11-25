using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;

namespace Synect.Application.OData;

/// <summary>Full-export state of one entity of a 1C:Fresh OData connection, read from the ingest journal (ingest_batches).</summary>
public sealed class ODataFullSyncState
{
    public Guid ConnectionId { get; set; }
    public string Entity { get; set; } = string.Empty;

    /// <summary>When the last COMPLETED full export of the entity ended (its last package; every sequence processed).</summary>
    public DateTimeOffset? LastFullSyncAt { get; set; }

    /// <summary>When the last full export of the entity was attempted (completed or not).</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>full (sent as a full export), incomplete_list, held_back (mass-deletion guard) or error.</summary>
    public string? LastOutcome { get; set; }

    /// <summary>Why the last attempt was not a full export (our own text: entity, counts, error type; never record content).</summary>
    public string? LastReason { get; set; }

    /// <summary>The last attempt has not completed (no completed full export since it).</summary>
    public bool LastAttemptFailed =>
        LastAttemptAt is { } attempt && (LastFullSyncAt is not { } full || attempt > full);
}

/// <summary>
/// Reads <see cref="ODataFullSyncState"/> from ingest_batches only (no table of its own). A full export is completed when its
/// package with is_last_batch and every sequence 1..N of that full_sync_id and entity were processed (the receiver then
/// marked deletions). Attempts are the packages whose envelope carries synect_odata.full_attempt: the OData loader writes
/// it on the first package of every entity it tried to export in full, and on an empty delta package when the attempt
/// ended as a delta or with an error.
/// </summary>
public static class ODataFullSyncStates
{
    public const string AttemptField = "full_attempt";

    public const string OutcomeFull = "full";
    public const string OutcomeIncompleteList = "incomplete_list";
    public const string OutcomeHeldBack = "held_back";
    public const string OutcomeError = "error";

    /// <summary>States of every entity that has pulled packages, of one connection or of all connections of the tenant.</summary>
    public static Task<List<ODataFullSyncState>> LoadAsync(ISynectDbContext db, Guid tenantId, Guid? connectionId, CancellationToken ct) =>
        db.Database.SqlQuery<ODataFullSyncState>($"""
            WITH b AS (
                SELECT connection_id, entity, full_sync_id, sequence, is_last_batch, mode, received_at, status, envelope_extra
                FROM ingest_batches
                WHERE tenant_id = {tenantId} AND ingest_key_id IS NULL AND ({connectionId}::uuid IS NULL OR connection_id = {connectionId}::uuid)),
            done AS (SELECT * FROM b WHERE status IN ('Processed', 'ProcessedWithErrors')),
            completed AS (
                SELECT l.connection_id, l.entity, max(l.received_at) AS at FROM done l
                WHERE l.mode = 'full' AND l.is_last_batch
                  AND (SELECT count(DISTINCT x.sequence) FROM done x WHERE x.full_sync_id = l.full_sync_id AND x.entity = l.entity) = l.sequence
                GROUP BY l.connection_id, l.entity),
            last_attempt AS (
                SELECT DISTINCT ON (connection_id, entity) connection_id, entity, received_at,
                       envelope_extra -> 'synect_odata' -> 'full_attempt' ->> 'outcome' AS outcome,
                       envelope_extra -> 'synect_odata' -> 'full_attempt' ->> 'reason' AS reason
                FROM b WHERE (envelope_extra -> 'synect_odata' -> 'full_attempt') IS NOT NULL
                ORDER BY connection_id, entity, received_at DESC, sequence DESC)
            SELECT e.connection_id, e.entity, c.at AS last_full_sync_at, la.received_at AS last_attempt_at,
                   la.outcome AS last_outcome, la.reason AS last_reason
            FROM (SELECT DISTINCT connection_id, entity FROM b) e
            LEFT JOIN completed c ON c.connection_id = e.connection_id AND c.entity = e.entity
            LEFT JOIN last_attempt la ON la.connection_id = e.connection_id AND la.entity = e.entity
            ORDER BY e.connection_id, e.entity
            """).ToListAsync(ct);

    /// <summary>
    /// The states of one connection for every entity of its profile (an entity with no package yet gets a state with every
    /// time null), followed by any entity the journal has that the profile no longer lists. An unreadable profile leaves the
    /// journal states only.
    /// </summary>
    public static List<ODataFullSyncState> ForProfile(Synect.Domain.Sync.Connection connection, IEnumerable<ODataFullSyncState> states, ODataOptions options)
    {
        var own = states.Where(s => s.ConnectionId == connection.Id).ToList();
        List<string> entities;
        try
        {
            entities = ODataProfileResolver.Resolve(connection.Settings, options).Entities
                .Select(e => Ingest.IngestSchema.Find(e.Entity)!.Name).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is ODataProfileException or System.Text.Json.JsonException)
        {
            return own;
        }
        var byEntity = own.ToDictionary(s => s.Entity, StringComparer.Ordinal);
        var result = entities.Select(e => byEntity.GetValueOrDefault(e) ?? new ODataFullSyncState { ConnectionId = connection.Id, Entity = e }).ToList();
        result.AddRange(own.Where(s => !entities.Contains(s.Entity, StringComparer.Ordinal)));
        return result;
    }

    /// <summary>
    /// Auto mode for one entity: full when its last completed full export is at least FullSyncInterval − FullSyncTolerance old
    /// (or there is none), unless the last attempt failed less than FullRetryAfter ago (then a delta).
    /// </summary>
    public static bool FullIsDue(ODataFullSyncState? state, DateTimeOffset now, ODataOptions o)
    {
        var due = state?.LastFullSyncAt is not { } last || now - last >= o.FullSyncInterval - o.FullSyncTolerance;
        if (!due) return false;
        return !(state is { LastAttemptFailed: true, LastAttemptAt: { } attempt } && now - attempt < o.FullRetryAfter);
    }
}
