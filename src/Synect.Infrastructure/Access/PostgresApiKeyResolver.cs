using Microsoft.EntityFrameworkCore;
using Synect.Application.Access;
using Synect.Infrastructure.Persistence;

namespace Synect.Infrastructure.Access;

/// <summary>
/// Calls synect_resolve_api_key (SECURITY DEFINER, owned by the schema owner). Runs without a tenant context:
/// the function matches exactly one non-revoked hash and returns only (id, tenant_id, scopes); every tenant
/// table stays under row-level security for the application role.
/// </summary>
public sealed class PostgresApiKeyResolver(SynectDbContext db) : IApiKeyResolver
{
    public async Task<ApiKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct)
    {
        if (keyHash.Length != 32) return null;
        var rows = await db.Database
            .SqlQuery<Row>($"SELECT api_key_id, tenant_id, scopes FROM synect_resolve_api_key({keyHash})")
            .ToListAsync(ct);
        return rows.Count == 1 ? new ApiKeyIdentity(rows[0].ApiKeyId, rows[0].TenantId, rows[0].Scopes) : null;
    }

    private sealed class Row
    {
        public Guid ApiKeyId { get; set; }
        public Guid TenantId { get; set; }
        public string[] Scopes { get; set; } = [];
    }
}
