using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;

namespace Synect.Application.Ingest;

/// <summary>Stored state of a record, enough to decide new / changed / unchanged / older.</summary>
public sealed class StoredRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset ModifiedAt { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

/// <summary>Version of a stored record as a pulled source reported it (extra.data_version), and its deletion mark.</summary>
public sealed class StoredVersion
{
    public string Ref { get; set; } = string.Empty;
    public string? DataVersion { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Set-based SQL of the receiver (PostgreSQL only), generated from <see cref="IngestSchema"/>: one statement per step and
/// package, rows passed as one jsonb parameter read with jsonb_to_recordset. Table and column names come only from the
/// schema (never from a package); every value is a bind parameter. Every statement also filters by tenant_id (row-level
/// security applies as well). Runs inside the package transaction.
/// </summary>
public sealed class IngestWriter(ISynectDbContext db)
{
    private static string Col(IngestField f) => f.Column;

    /// <summary>Text key of a stored row, the same text <see cref="IngestItemParser.BuildKey"/> builds.</summary>
    public static string KeyExpression(IngestEntitySpec spec, string alias)
    {
        if (spec.HasRef) return $"{alias}.ref";
        return string.Join(" || chr(31) || ", spec.Keys.Select(k => k.Type == IngestFieldType.Date
            ? $"coalesce(to_char({alias}.{k.Column}, 'YYYY-MM-DD'), '')"
            : $"coalesce({alias}.{k.Column}::text, '')"));
    }

    private static string KeyJoin(IngestEntitySpec spec, string t, string k) =>
        string.Join(" AND ", spec.Keys.Select(f => f.Required ? $"{t}.{f.Column} = {k}.{f.Column}" : $"{t}.{f.Column} IS NOT DISTINCT FROM {k}.{f.Column}"));

    private static string KeyRecord(IngestEntitySpec spec) => string.Join(", ", spec.Keys.Select(f => $"{f.Column} {f.PgType}"));

    public async Task<Dictionary<string, StoredRecord>> LoadAsync(IngestEntitySpec spec, Guid tenant, IReadOnlyList<ParsedItem> items, CancellationToken ct)
    {
        var result = new Dictionary<string, StoredRecord>(StringComparer.Ordinal);
        if (items.Count == 0) return result;
        var keys = Rows(items.DistinctBy(i => i.Key).Select(i => spec.Keys.ToDictionary(k => k.Column, k => i.Values.GetValueOrDefault(k.Column))));
        var sql = $$"""
            SELECT {{KeyExpression(spec, "t")}} AS key, t.modified_at, t.content_hash, t.is_deleted
            FROM {{spec.Table}} t
            JOIN jsonb_to_recordset({0}::jsonb) AS k({{KeyRecord(spec)}}) ON {{KeyJoin(spec, "t", "k")}}
            WHERE t.tenant_id = {1}
            """;
        foreach (var row in await db.Database.SqlQueryRaw<StoredRecord>(sql, keys, tenant).ToListAsync(ct))
            result[row.Key] = row;
        return result;
    }

    /// <summary>Inserts new records and updates changed ones; a stored newer version is never overwritten.</summary>
    public async Task<int> UpsertAsync(IngestEntitySpec spec, Guid tenant, Guid batch, int schemaVersion, DateTimeOffset now,
        IReadOnlyList<ParsedItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return 0;
        var fields = spec.WriteColumns;
        var rows = Rows(items.Select(i =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Guid.CreateVersion7() };
            foreach (var f in fields) row[f.Column] = i.Values.GetValueOrDefault(f.Column);
            row["is_deleted"] = i.IsDeleted;
            row["modified_at"] = i.ModifiedAt;
            row["content_hash"] = i.ContentHash;
            row["extra"] = new RawJson(i.ExtraJson);
            return row;
        }));
        var keyColumns = spec.Keys.Select(Col).ToHashSet(StringComparer.Ordinal);
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var selected = string.Join(", ", fields.Select(f => f.PgType == "text[]" ? $"coalesce(x.{f.Column}, ARRAY[]::text[])" : "x." + f.Column));
        var record = string.Join(", ", fields.Select(f => $"{f.Column} {f.PgType}"));
        var updates = string.Join(", ", fields.Where(f => !keyColumns.Contains(f.Column)).Select(f => $"{f.Column} = excluded.{f.Column}"));
        var conflict = string.Join(", ", spec.Keys.Select(Col));
        var sql = $$"""
            INSERT INTO {{spec.Table}} AS t (id, tenant_id, {{columns}}, is_deleted, modified_at, content_hash, extra, schema_version,
                last_batch_id, first_seen_at, last_seen_at, updated_at)
            SELECT x.id, {0}, {{selected}}, x.is_deleted, x.modified_at, x.content_hash, coalesce(x.extra, jsonb_build_object()), {1},
                {2}, {3}, {3}, {3}
            FROM jsonb_to_recordset({4}::jsonb) AS x(id uuid, {{record}}, is_deleted boolean, modified_at timestamptz, content_hash text, extra jsonb)
            ON CONFLICT (tenant_id, {{conflict}}) DO UPDATE SET {{updates}}{{(updates.Length > 0 ? ", " : "")}}
                is_deleted = excluded.is_deleted, modified_at = excluded.modified_at, content_hash = excluded.content_hash,
                extra = excluded.extra, schema_version = excluded.schema_version, last_batch_id = excluded.last_batch_id,
                last_seen_at = excluded.last_seen_at, updated_at = excluded.updated_at
            WHERE t.modified_at <= excluded.modified_at
            """;
        return await db.Database.ExecuteSqlRawAsync(sql, [tenant, schemaVersion, batch, now.ToUniversalTime(), rows], ct);
    }

    /// <summary>Deletion notices (only a ref, or the key of a current-state row): marks known records deleted, keeping their other fields.</summary>
    public async Task<int> MarkDeletedAsync(IngestEntitySpec spec, Guid tenant, Guid batch, int schemaVersion, DateTimeOffset now,
        IReadOnlyList<ParsedItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return 0;
        var rows = Rows(items.Select(i =>
        {
            var row = spec.Keys.ToDictionary(k => k.Column, k => i.Values.GetValueOrDefault(k.Column), StringComparer.Ordinal);
            row["modified_at"] = i.ModifiedAt;
            row["content_hash"] = i.ContentHash;
            return (IReadOnlyDictionary<string, object?>)row;
        }));
        var sql = $$"""
            UPDATE {{spec.Table}} t SET is_deleted = true, modified_at = x.modified_at, content_hash = x.content_hash,
                schema_version = {1}, last_batch_id = {2}, last_seen_at = {3}, updated_at = {3}
            FROM jsonb_to_recordset({4}::jsonb) AS x({{KeyRecord(spec)}}, modified_at timestamptz, content_hash text)
            WHERE t.tenant_id = {0} AND {{KeyJoin(spec, "t", "x")}} AND t.modified_at <= x.modified_at
            """;
        return await db.Database.ExecuteSqlRawAsync(sql, [tenant, schemaVersion, batch, now.ToUniversalTime(), rows], ct);
    }

    /// <summary>Received but unchanged records: last_seen_at only, at most once an hour (no rewrite of the content).</summary>
    public async Task TouchAsync(IngestEntitySpec spec, Guid tenant, DateTimeOffset now, IReadOnlyList<ParsedItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var keys = Rows(items.DistinctBy(i => i.Key).Select(i => spec.Keys.ToDictionary(k => k.Column, k => i.Values.GetValueOrDefault(k.Column))));
        var sql = $$"""
            UPDATE {{spec.Table}} t SET last_seen_at = {1}
            FROM jsonb_to_recordset({2}::jsonb) AS k({{KeyRecord(spec)}})
            WHERE t.tenant_id = {0} AND {{KeyJoin(spec, "t", "k")}} AND t.last_seen_at < {3}
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [tenant, now.ToUniversalTime(), keys, now.ToUniversalTime().AddHours(-1)], ct);
    }

    /// <summary>Time of the last accepted data of a group of a 1C connection (ingest_freshness); never moves back.</summary>
    public Task MarkFreshAsync(Guid tenant, Guid connection, Synect.Domain.Accounting.IngestDataGroup group, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO ingest_freshness (tenant_id, connection_id, data_group, last_accepted_at)
            VALUES ({tenant}, {connection}, {group.ToString()}, {now.ToUniversalTime()})
            ON CONFLICT (tenant_id, connection_id, data_group) DO UPDATE SET last_accepted_at = excluded.last_accepted_at
            WHERE ingest_freshness.last_accepted_at < excluded.last_accepted_at
            """, ct);

    /// <summary>
    /// Records that still exist in a pulled source and were not sent again (their version did not change): last_seen_at
    /// only, at most once an hour, like <see cref="TouchAsync"/>. Entities keyed by ref only.
    /// </summary>
    public async Task TouchRefsAsync(IngestEntitySpec spec, Guid tenant, DateTimeOffset now, IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        if (refs.Count == 0 || !spec.HasRef) return;
        var sql = $$"""
            UPDATE {{spec.Table}} t SET last_seen_at = {1}
            WHERE t.tenant_id = {0} AND t.ref = ANY({2}) AND t.last_seen_at < {3}
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [tenant, now.ToUniversalTime(), refs.ToArray(), now.ToUniversalTime().AddHours(-1)], ct);
    }

    /// <summary>
    /// data_version (kept in extra by the OData loader) and is_deleted of stored records of an entity keyed by ref: all of
    /// them, or only <paramref name="refs"/>. Read-only.
    /// </summary>
    public async Task<Dictionary<string, StoredVersion>> LoadVersionsAsync(IngestEntitySpec spec, Guid tenant, IReadOnlyCollection<string>? refs,
        CancellationToken ct)
    {
        if (!spec.HasRef) throw new InvalidOperationException($"Entity {spec.Name} is not keyed by ref.");
        var result = new Dictionary<string, StoredVersion>(StringComparer.Ordinal);
        if (refs is { Count: 0 }) return result;
        // The table name comes from the schema only; every value is a bind parameter.
        var sql = "SELECT t.ref AS ref, t.extra->>'data_version' AS data_version, t.is_deleted AS is_deleted FROM " + spec.Table
                  + " t WHERE t.tenant_id = {0}" + (refs is null ? "" : " AND t.ref = ANY({1})");
        object[] parameters = refs is null ? [tenant] : [tenant, refs.ToArray()];
        var rows = await db.Database.SqlQueryRaw<StoredVersion>(sql, parameters).ToListAsync(ct);
        foreach (var row in rows) result[row.Ref] = row;
        return result;
    }

    /// <summary>
    /// Replaces the lines (settlements) of the given documents as a whole. <paramref name="existing"/> are the documents
    /// that were stored before this package: only they can have rows to remove.
    /// </summary>
    public async Task ReplaceChildrenAsync(IngestEntitySpec spec, Guid tenant, IReadOnlyList<ParsedItem> items, IReadOnlySet<string> existing,
        CancellationToken ct)
    {
        if (spec.Children is not { } child || items.Count == 0) return;
        var refs = items.Select(i => i.Key!).Where(existing.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (refs.Length > 0)
        {
            // The table name comes from the schema, never from a package.
            var delete = $"DELETE FROM {child.Table} WHERE tenant_id = {{0}} AND document_ref = ANY({{1}})";
            await db.Database.ExecuteSqlRawAsync(delete, [tenant, refs], ct);
        }
        var rows = items.SelectMany(i => i.Children!.Select(c =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["document_ref"] = i.Key };
            foreach (var f in child.Fields) row[f.Column] = c.GetValueOrDefault(f.Column);
            row["extra"] = new RawJson((string)c["extra"]!);
            return row;
        })).ToList();
        if (rows.Count == 0) return;
        var columns = string.Join(", ", child.Fields.Select(Col));
        var selected = string.Join(", ", child.Fields.Select(f => "x." + f.Column));
        var record = string.Join(", ", child.Fields.Select(f => $"{f.Column} {f.PgType}"));
        var sql = $$"""
            INSERT INTO {{child.Table}} (tenant_id, document_ref, {{columns}}, extra)
            SELECT {0}, x.document_ref, {{selected}}, coalesce(x.extra, jsonb_build_object())
            FROM jsonb_to_recordset({1}::jsonb) AS x(document_ref text, {{record}}, extra jsonb)
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [tenant, Rows(rows)], ct);
    }

    /// <summary>
    /// Raw layer (raw_objects, one row per object and type): the item as received, with its already computed content hash.
    /// One statement; the last record of a repeated (type, id) must be passed once.
    /// </summary>
    public async Task UpsertRawAsync(Guid tenant, Guid connection, string sourceCode, IReadOnlyList<(string Type, string Id, string Json, string Hash)> records,
        DateTimeOffset now, CancellationToken ct)
    {
        if (records.Count == 0) return;
        var ids = records.Select(_ => Guid.CreateVersion7()).ToArray();
        var types = records.Select(r => r.Type).ToArray();
        var external = records.Select(r => r.Id).ToArray();
        var payloads = records.Select(r => r.Json).ToArray();
        var hashes = records.Select(r => r.Hash).ToArray();
        var at = now.ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO raw_objects (id, tenant_id, connection_id, source_code, object_type, external_id, payload, hash, first_seen_at, last_seen_at, updated_at)
            SELECT u.id, {tenant}, {connection}, {sourceCode}, u.t, u.e, u.p::jsonb, u.h, {at}, {at}, {at}
            FROM unnest({ids}, {types}, {external}, {payloads}, {hashes}) AS u(id, t, e, p, h)
            ON CONFLICT (tenant_id, connection_id, object_type, external_id) DO UPDATE SET
                last_seen_at = excluded.last_seen_at, payload = excluded.payload, updated_at = excluded.updated_at, hash = excluded.hash
            """, ct);
    }

    /// <summary>Keys of a full export received so far (all items with a readable key, refused ones too: they exist in 1C).</summary>
    public async Task TrackFullSyncAsync(Guid tenant, string fullSyncId, string entity, IReadOnlyCollection<string> keys, DateTimeOffset now, CancellationToken ct)
    {
        if (keys.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ingest_full_sync_items (tenant_id, full_sync_id, entity, item_key, created_at)
            SELECT {0}, {1}, {2}, k, {3} FROM unnest({4}) AS k
            ON CONFLICT DO NOTHING
            """, [tenant, fullSyncId, entity, now.ToUniversalTime(), keys.ToArray()], ct);
    }

    /// <summary>
    /// Completes a full export: records of the entity that were not in it and were not received since the export started
    /// are marked deleted (balances: only on the dates the export covered). Then the export's key list is removed, together
    /// with key lists of abandoned exports older than <paramref name="retention"/>.
    /// </summary>
    public async Task<int> CompleteFullSyncAsync(IngestEntitySpec spec, Guid tenant, Guid batch, string fullSyncId, DateTimeOffset startedAt,
        DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        if (spec.CurrentState) return await RemoveMissingRowsAsync(spec, tenant, batch, fullSyncId, startedAt, now, retention, ct);
        var dates = !spec.KeyedByDate ? "" : $$"""
             AND t.as_of_date IN (SELECT DISTINCT left(s.item_key, 10)::date FROM ingest_full_sync_items s
                                  WHERE s.tenant_id = {0} AND s.full_sync_id = {1} AND s.entity = {2})
            """;
        var sql = $$"""
            UPDATE {{spec.Table}} t SET is_deleted = true, updated_at = {3}, last_batch_id = {4}
            WHERE t.tenant_id = {0} AND NOT t.is_deleted AND t.last_seen_at < {5}{{dates}}
              AND NOT EXISTS (SELECT 1 FROM ingest_full_sync_items s
                              WHERE s.tenant_id = {0} AND s.full_sync_id = {1} AND s.entity = {2} AND s.item_key = {{KeyExpression(spec, "t")}})
            """;
        var marked = await db.Database.ExecuteSqlRawAsync(sql, [tenant, fullSyncId, spec.Name, now.ToUniversalTime(), batch, startedAt.ToUniversalTime()], ct);
        await ForgetFullSyncAsync(spec, tenant, fullSyncId, now, retention, ct);
        return marked;
    }

    /// <summary>
    /// Completes a full export of a current-state table (catalog_prices, catalog_stock): rows that were not in it and were not
    /// received since the export started are DELETED (the table holds the current state only; a kept row would be read as a
    /// price or stock), and each removed row is logged in ingest_removed_rows with its key and the package, never its
    /// content. Called only from the package with is_last_batch, for rows absent from THIS full_sync_id.
    /// </summary>
    private async Task<int> RemoveMissingRowsAsync(IngestEntitySpec spec, Guid tenant, Guid batch, string fullSyncId, DateTimeOffset startedAt,
        DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        var sql = $$"""
            WITH removed AS (
                DELETE FROM {{spec.Table}} t
                WHERE t.tenant_id = {0} AND t.last_seen_at < {5}
                  AND NOT EXISTS (SELECT 1 FROM ingest_full_sync_items s
                                  WHERE s.tenant_id = {0} AND s.full_sync_id = {1} AND s.entity = {2} AND s.item_key = {{KeyExpression(spec, "t")}})
                RETURNING {{KeyExpression(spec, "t")}} AS item_key)
            INSERT INTO ingest_removed_rows (tenant_id, id, ingest_batch_id, full_sync_id, entity, item_key, removed_at)
            SELECT {0}, gen_random_uuid(), {4}, {1}, {2}, r.item_key, {3} FROM removed r
            """;
        var removed = await db.Database.ExecuteSqlRawAsync(sql, [tenant, fullSyncId, spec.Name, now.ToUniversalTime(), batch, startedAt.ToUniversalTime()], ct);
        await ForgetFullSyncAsync(spec, tenant, fullSyncId, now, retention, ct);
        return removed;
    }

    /// <summary>Removes the key list of a completed export and those of abandoned exports older than <paramref name="retention"/>.</summary>
    private Task ForgetFullSyncAsync(IngestEntitySpec spec, Guid tenant, string fullSyncId, DateTimeOffset now, TimeSpan retention, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ingest_full_sync_items WHERE tenant_id = {0} AND ((full_sync_id = {1} AND entity = {2}) OR created_at < {3})",
            [tenant, fullSyncId, spec.Name, (now - retention).ToUniversalTime()], ct);

    /// <summary>A JSON value written as is (jsonb columns).</summary>
    private sealed record RawJson(string Json);

    /// <summary>JSON array of row objects for jsonb_to_recordset.</summary>
    private static string Rows(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                foreach (var (name, value) in row)
                {
                    w.WritePropertyName(name);
                    switch (value)
                    {
                        case null: w.WriteNullValue(); break;
                        case string s: w.WriteStringValue(s); break;
                        case string[] list:
                            w.WriteStartArray();
                            foreach (var item in list) w.WriteStringValue(item);
                            w.WriteEndArray();
                            break;
                        case bool b: w.WriteBooleanValue(b); break;
                        case long l: w.WriteNumberValue(l); break;
                        case int n: w.WriteNumberValue(n); break;
                        case decimal d: w.WriteNumberValue(d); break;
                        case Guid g: w.WriteStringValue(g); break;
                        case DateOnly date: w.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
                        case DateTimeOffset at: w.WriteStringValue(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); break;
                        case RawJson raw: w.WriteRawValue(raw.Json, skipInputValidation: false); break;
                        default: throw new InvalidOperationException($"Unsupported value type {value.GetType().Name}.");
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
