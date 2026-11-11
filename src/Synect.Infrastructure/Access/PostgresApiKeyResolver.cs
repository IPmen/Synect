using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synect.Application.Access;
using Synect.Infrastructure.Persistence;

namespace Synect.Infrastructure.Access;

/// <summary>
/// Calls the SECURITY DEFINER functions owned by the schema owner. Runs without a tenant context:
/// synect_resolve_api_key matches exactly one usable hash and returns only (id, account_id, tenant_id, scopes);
/// synect_api_key_organizations returns only (tenant_id, name, tin) of the organizations that key may see. Every tenant
/// table stays under row-level security for the application role; the account tables are not readable by it at all.
/// </summary>
public sealed class PostgresApiKeyResolver(SynectDbContext db) : IApiKeyResolver
{
    public async Task<ApiKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct)
    {
        if (keyHash.Length != 32) return null;
        var rows = await db.Database
            .SqlQuery<KeyRow>($"SELECT api_key_id, account_id, tenant_id, scopes FROM synect_resolve_api_key({keyHash})")
            .ToListAsync(ct);
        return rows.Count == 1 ? new ApiKeyIdentity(rows[0].ApiKeyId, rows[0].AccountId, rows[0].TenantId, rows[0].Scopes) : null;
    }

    public async Task<IReadOnlyList<OrganizationInfo>> OrganizationsAsync(byte[] keyHash, CancellationToken ct)
    {
        if (keyHash.Length != 32) return [];
        var rows = await db.Database
            .SqlQuery<OrganizationRow>($"SELECT tenant_id, name, tin FROM synect_api_key_organizations({keyHash})")
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.TenantId)
            .Select(r => new OrganizationInfo(r.TenantId, r.Name, r.Tin)).ToList();
    }

    private sealed class KeyRow
    {
        public Guid ApiKeyId { get; set; }
        public Guid? AccountId { get; set; }
        public Guid? TenantId { get; set; }
        public string[] Scopes { get; set; } = [];
    }

    private sealed class OrganizationRow
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Tin { get; set; }
    }
}

/// <summary>
/// account_tool_audit_log through synect_write_account_tool_audit, in its own scope (the request's context may be in a
/// failed state). A failure to write is logged without arguments and never replaces the answer.
/// </summary>
public sealed class PostgresAccountToolAuditWriter(
    IServiceScopeFactory scopes, ILogger<PostgresAccountToolAuditWriter> logger) : IAccountToolAuditWriter
{
    public async Task WriteAsync(byte[] keyHash, AccountToolAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SynectDbContext>();
            var duration = (int)Math.Clamp(entry.Duration.TotalMilliseconds, 0, int.MaxValue);
            var status = entry.Status.ToString();
            var tenants = entry.TenantIds.ToArray();
            var parameters = entry.ParametersJson;
            var details = entry.DetailsJson;
            await db.Database.SqlQuery<bool>($"""
                SELECT synect_write_account_tool_audit({keyHash}, {entry.Tool}, {parameters}::jsonb, {entry.StartedAt}, {duration},
                    {entry.RowCount}, {status}, {entry.ErrorMessage}, {details}::jsonb, {tenants}) AS "Value"
                """).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not write account_tool_audit_log for tool {Tool}: {ErrorType}", entry.Tool, ex.GetType().Name);
        }
    }
}

/// <summary>
/// Calls the SECURITY DEFINER function synect_resolve_ingest_key (owned by the schema owner, search_path pinned, EXECUTE only
/// for the application role). It matches one usable hash and returns only (ingest_key_id, tenant_id, connection_id,
/// tenant_tin). ingest_keys stays under row-level security for the application role.
/// </summary>
public sealed class PostgresIngestKeyResolver(SynectDbContext db) : Synect.Application.Ingest.IIngestKeyResolver
{
    public async Task<Synect.Application.Ingest.IngestKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct)
    {
        if (keyHash.Length != 32) return null;
        var rows = await db.Database
            .SqlQuery<IngestKeyRow>($"SELECT ingest_key_id, tenant_id, connection_id, tenant_tin FROM synect_resolve_ingest_key({keyHash})")
            .ToListAsync(ct);
        return rows.Count == 1
            ? new Synect.Application.Ingest.IngestKeyIdentity(rows[0].IngestKeyId, rows[0].TenantId, rows[0].ConnectionId, rows[0].TenantTin)
            : null;
    }

    private sealed class IngestKeyRow
    {
        public Guid IngestKeyId { get; set; }
        public Guid TenantId { get; set; }
        public Guid ConnectionId { get; set; }
        public string? TenantTin { get; set; }
    }
}
