using Synect.Domain.Common;

namespace Synect.Domain.Accounting;

/// <summary>Result of one received package (ingest_batches.status).</summary>
public enum IngestBatchStatus
{
    /// <summary>Every item was accepted or unchanged.</summary>
    Processed = 1,

    /// <summary>The items were processed and at least one was rejected (possibly all of them).</summary>
    ProcessedWithErrors = 2,

    /// <summary>The package itself was refused (envelope, schema version, organization); no item was processed.</summary>
    Rejected = 3,

    /// <summary>A system error rolled the package back; it may be sent again under the same batch_id and sequence.</summary>
    Failed = 4,
}

/// <summary>
/// Journal of received 1C packages (ingest_batches). One row per (organization, batch_id, sequence): a package sent again
/// after it was processed is answered from this row ("duplicate"); a Rejected or Failed row is reused by the next attempt.
/// Holds counters and identifiers only, never item content (rejected items: index, key, code and our own message).
/// </summary>
public class IngestBatch : TenantEntity
{
    public const int MaxIdLength = 64;

    public Guid ConnectionId { get; set; }
    public Guid IngestKeyId { get; set; }

    /// <summary>Package id assigned by the source.</summary>
    public string BatchId { get; set; } = string.Empty;

    public int Sequence { get; set; }
    public string Entity { get; set; } = string.Empty;

    /// <summary>full or delta.</summary>
    public string Mode { get; set; } = string.Empty;

    public string? FullSyncId { get; set; }
    public bool IsLastBatch { get; set; }
    public int SchemaVersion { get; set; }
    public string? ExtensionVersion { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ItemsTotal { get; set; }
    public int Accepted { get; set; }
    public int Unchanged { get; set; }
    public int Rejected { get; set; }

    /// <summary>Records marked deleted because a completed full export did not contain them.</summary>
    public int MarkedDeleted { get; set; }

    /// <summary>
    /// Rows of current-state tables (catalog_prices, catalog_stock) removed because a completed full export did not contain
    /// them; each one is listed in ingest_removed_rows.
    /// </summary>
    public int RemovedRows { get; set; }

    public IngestBatchStatus Status { get; set; }

    /// <summary>Error code and our own message (never item content).</summary>
    public string? Error { get; set; }

    /// <summary>SHA-256 (hex) of the request body after decompression: tells a repeated package from a different one.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    public int PayloadBytes { get; set; }

    /// <summary>Rejected items as a jsonb array of {index, key, code, message}.</summary>
    public string? ItemErrors { get; set; }

    /// <summary>Envelope fields Synect does not know (jsonb object, as received).</summary>
    public string? EnvelopeExtra { get; set; }

    /// <summary>How many times this (batch_id, sequence) was received.</summary>
    public int Attempts { get; set; } = 1;
}

/// <summary>
/// A key the 1C extension uses to send packages (ingest_keys). Only the SHA-256 of the key is stored; the key is shown once.
/// It belongs to one organization and its 1C connection; it never works on /mcp, and MCP keys never work on the ingest route.
/// Found before the organization is known only through the SECURITY DEFINER function synect_resolve_ingest_key.
/// </summary>
public class IngestKey : TenantEntity
{
    public Guid ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the UTF-8 key string (32 bytes).</summary>
    public byte[] KeyHash { get; set; } = [];

    /// <summary>First characters of the key, to recognise it in lists (not enough to use it).</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Keys of the items received in a full export that is still in progress (ingest_full_sync_items). When the package with
/// is_last_batch arrives and every sequence of the export was processed, records of the entity that are not listed here
/// are marked deleted and the rows of that export are removed.
/// </summary>
public class IngestFullSyncItem : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string FullSyncId { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string ItemKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The two kinds of 1C data, received at different rates and judged fresh by different thresholds.</summary>
public enum IngestDataGroup
{
    /// <summary>Accounting data (counterparties, contracts, documents, bank documents, balances): about once an hour.</summary>
    Accounting = 1,

    /// <summary>Catalog for the Mailo bot (items, categories, prices, warehouses, stock): changes every minute, full daily.</summary>
    Catalog = 2,
}

/// <summary>
/// Time of the last accepted package of one data group of a 1C connection (ingest_freshness), so that frequent catalog
/// packages never make stale accounting data look fresh. One row per (organization, connection, group).
/// </summary>
public class IngestFreshness : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public IngestDataGroup DataGroup { get; set; }
    public DateTimeOffset LastAcceptedAt { get; set; }
}

/// <summary>
/// A row removed from a current-state table (catalog_prices, catalog_stock) because a completed full export did not contain
/// it (ingest_removed_rows). Identifiers only (the key of the row: refs joined with U+001F), never the content.
/// </summary>
public class IngestRemovedRow : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid IngestBatchId { get; set; }
    public string FullSyncId { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string ItemKey { get; set; } = string.Empty;
    public DateTimeOffset RemovedAt { get; set; }
}
