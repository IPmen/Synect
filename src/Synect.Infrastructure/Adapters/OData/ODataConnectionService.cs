using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.OData;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Adapters.OData;

public sealed record ODataConnectionResult(Guid ConnectionId, string Name, string HostLabel, string Profile, ReactivationResult? Test);

/// <summary>
/// Creates the 1C:Fresh OData connection of the current organization (tenant context required): base_url checked by
/// <see cref="ODataEndpoint"/> (HTTPS, allowed host, no internal address), the profile checked, login and password into
/// the encrypted secret store only. At most one 1C connection (extension or OData) may be active: a second one is refused
/// (<see cref="OneCConnectionRule"/> and the unique index). Disabling a connection is the explicit way to change the source.
/// </summary>
public sealed class ODataConnectionService(
    ISynectDbContext db,
    ITenantContext tenant,
    ISecretStore secrets,
    SyncRequestService requests,
    IOptions<ODataOptions> options,
    TimeProvider time,
    ILogger<ODataConnectionService> logger)
{
    public const string DefaultName = "1C:Fresh (OData)";

    public async Task<ODataConnectionResult> CreateAsync(string? name, string baseUrl, string login, SecretValue password, string? profile,
        string? organizationKey, bool test, CancellationToken ct)
    {
        RequireTenant();
        var root = ODataEndpoint.Normalize(baseUrl, options.Value);
        var profileName = string.IsNullOrWhiteSpace(profile) ? options.Value.DefaultProfile : profile.Trim();
        ODataProfileResolver.ByName(profileName, options.Value); // must exist and be valid
        if (organizationKey is not null && !Guid.TryParse(organizationKey, out _))
            throw new ArgumentException("--organization-key must be the GUID (Ref_Key) of the organization in the application.");
        var cleanLogin = (login ?? "").Trim();
        if (cleanLogin.Length is 0 or > 256 || cleanLogin.Any(char.IsControl)) throw new ArgumentException("A login of 1 to 256 characters is required.");
        var plain = password.Reveal();
        if (plain.Length is 0 or > 1024) throw new ArgumentException("A password of 1 to 1024 characters is required (SYNECT_ODATA_PASSWORD).");
        var connectionName = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        if (connectionName.Length > 256) throw new ArgumentException("The connection name is longer than 256 characters.");

        var hostLabel = ODataEndpoint.HostLabel(root);
        var settings = new JsonObject
        {
            ["base_url"] = root.ToString(),
            ["host_label"] = hostLabel,
            ["profile"] = profileName,
        };
        if (organizationKey is not null) settings["organization_key"] = Guid.Parse(organizationKey).ToString("D");

        Connection connection;
        try
        {
            connection = await db.InTransactionAsync(async token =>
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"onec-connection:" + tenant.TenantId}, 0))", token);
                await OneCConnectionRule.EnsureNoOtherActiveAsync(db, null, token);
                if (await db.Connections.AnyAsync(c => c.Name == connectionName, token))
                    throw new InvalidOperationException($"The organization already has a connection named '{connectionName}'. Choose another --name.");
                var created = new Connection
                {
                    Name = connectionName,
                    SourceCode = Connection.OneCFreshODataSourceCode,
                    AdapterKind = AdapterKind.Accounting1C,
                    Settings = settings.ToJsonString(),
                    CreatedAt = time.GetUtcNow(),
                };
                if (test) created.MarkNeedsAttention("Waiting for the connection test after the credentials were entered.", time.GetUtcNow());
                db.Connections.Add(created);
                await db.SaveChangesAsync(token);
                await secrets.SetAsync(created.Id, ConnectionSecret.CredentialsKind, LoginPasswordCredentials.Create(cleanLogin, plain), token);
                return created;
            }, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName == Persistence.Configurations.ConnectionConfiguration.OneActiveOneCIndex)
        {
            throw new OneCConnectionConflictException("The organization already has an active 1C connection; disable it first (disable-connection).");
        }

        logger.LogInformation("1C:Fresh OData connection {ConnectionId} created for tenant {TenantId} (host label {HostLabel}, profile {Profile})",
            connection.Id, connection.TenantId, hostLabel, profileName);
        var result = test ? await requests.ReactivateAsync(connection.Id, ct) : null;
        return new ODataConnectionResult(connection.Id, connection.Name, hostLabel, profileName, result);
    }

    /// <summary>Disables a connection of any kind (found by id or name). Its data stays; it is no longer loaded or accepted.</summary>
    public Task<Connection> DisableAsync(string idOrName, string? reason, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            RequireTenant();
            var key = idOrName.Trim();
            var c = Guid.TryParse(key, out var id)
                ? await db.Connections.SingleOrDefaultAsync(x => x.Id == id, token)
                : await db.Connections.SingleOrDefaultAsync(x => x.Name == key, token);
            if (c is null) throw new KeyNotFoundException($"Connection '{key}' not found for this organization.");
            c.Disable(string.IsNullOrWhiteSpace(reason) ? "Disabled by an administrator." : reason.Trim(), time.GetUtcNow());
            var jobs = await db.SyncJobs.Where(j => j.ConnectionId == c.Id && j.Status == SyncJobStatus.Queued).ToListAsync(token);
            foreach (var j in jobs) j.Finish(SyncJobStatus.Cancelled, time.GetUtcNow(), null, "Connection disabled.");
            await db.SaveChangesAsync(token);
            logger.LogInformation("Connection {ConnectionId} ({Kind}) disabled for tenant {TenantId}", c.Id, c.Kind, c.TenantId);
            return c;
        }, ct);

    private void RequireTenant()
    {
        if (tenant.TenantId is null) throw new InvalidOperationException("Connections are managed in a tenant context.");
    }
}
