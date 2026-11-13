using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Adapters;

/// <summary>A connection as listed for administration: never the secret, the login only masked.</summary>
public sealed record ConnectionCredentialsInfo(
    Guid Id, string Name, string SourceCode, AdapterKind Kind, ConnectionStatus Status, string? StatusReason, bool HasCredentials,
    string? LoginMasked, int? SecretVersion, DateTimeOffset? SecretCreatedAt, DateTimeOffset? SecretRotatedAt, DateTimeOffset? LastSuccessfulSyncAt);

/// <summary>Result of creating a connection or replacing its credentials; with a test, what the test said.</summary>
public sealed record ConnectionCredentialsResult(Guid ConnectionId, string Name, string SourceCode, AdapterKind Kind, bool Created, int SecretVersion,
    ReactivationResult? Test);

/// <summary>
/// Enters the access of a client organization to a bank or Didox (tenant context required): creates the connection of a
/// registered adapter with its login and password, replaces the credentials of an existing connection (a rotation), tests
/// them and lists connections. The password arrives as a <see cref="SecretValue"/> and goes only into the encrypted secret
/// store (connection_secrets, Data Protection); it is never logged, printed or returned. The login is stored in the same
/// encrypted secret and shown only masked.
/// </summary>
/// <remarks>
/// A test runs through <see cref="SyncRequestService.ReactivateAsync"/>: the connection is put into NeedsAttention
/// ("waiting for the connection test"), the adapter's TestConnection is called with the stored credentials, and on
/// success the connection becomes Active and its first load is queued; on failure it stays in NeedsAttention with the
/// reason (and is not scheduled). Without a test a new connection is Active and loaded on the schedule.
/// </remarks>
public sealed class ConnectionCredentialsService(
    ISynectDbContext db,
    ITenantContext tenant,
    IAdapterRegistry registry,
    ISecretStore secrets,
    SyncRequestService requests,
    TimeProvider time,
    ILogger<ConnectionCredentialsService> logger)
{
    public const int MaxLoginLength = 256;
    public const int MaxPasswordLength = 1024;
    public const string WaitingForTest = "Waiting for the connection test after the credentials were entered.";

    public Task<ConnectionCredentialsResult> CreateAsync(string sourceCode, string? name, string login, SecretValue password, bool test, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var adapter = Adapter(sourceCode);
            var (_, credentials) = Credentials(login, password);
            var connectionName = string.IsNullOrWhiteSpace(name) ? adapter.ProviderName : name.Trim();
            if (connectionName.Length > 256) throw new ArgumentException("The connection name is longer than 256 characters.");

            var connection = await db.InTransactionAsync(async token =>
            {
                if (await db.Connections.AnyAsync(c => c.Name == connectionName, token))
                    throw new InvalidOperationException($"The organization already has a connection named '{connectionName}'. Choose another --name, or use set-credentials to replace its access.");
                var created = new Connection
                {
                    Name = connectionName,
                    SourceCode = adapter.SourceCode,
                    AdapterKind = adapter.Kind,
                    CreatedAt = time.GetUtcNow(),
                };
                // With a test the connection is not scheduled until the test succeeds.
                if (test) created.MarkNeedsAttention(WaitingForTest, time.GetUtcNow());
                db.Connections.Add(created);
                await db.SaveChangesAsync(token);
                await secrets.SetAsync(created.Id, ConnectionSecret.CredentialsKind, credentials, token);
                return created;
            }, ct);
            logger.LogInformation("Connection {ConnectionId} ({SourceCode}) created with credentials for tenant {TenantId}", connection.Id, connection.SourceCode, connection.TenantId);
            var result = test ? await requests.ReactivateAsync(connection.Id, ct) : null;
            return new ConnectionCredentialsResult(connection.Id, connection.Name, connection.SourceCode, connection.AdapterKind, true, 1, result);
        });

    /// <summary>Replaces the login and password of a bank or Didox connection (found by id or name); the secret version grows.</summary>
    public Task<ConnectionCredentialsResult> SetCredentialsAsync(string connectionIdOrName, string login, SecretValue password, bool test, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var (_, credentials) = Credentials(login, password);
            var (connection, version) = await db.InTransactionAsync(async token =>
            {
                var c = await FindAsync(connectionIdOrName, token);
                await secrets.SetAsync(c.Id, ConnectionSecret.CredentialsKind, credentials, token);
                if (test && c.Status == ConnectionStatus.Active)
                {
                    c.MarkNeedsAttention(WaitingForTest, time.GetUtcNow());
                    await db.SaveChangesAsync(token);
                }
                var v = await db.ConnectionSecrets.AsNoTracking()
                    .Where(s => s.ConnectionId == c.Id && s.Kind == ConnectionSecret.CredentialsKind).Select(s => s.Version).SingleAsync(token);
                return (c, v);
            }, ct);
            logger.LogInformation("Credentials of connection {ConnectionId} replaced (version {Version})", connection.Id, version);
            var result = test ? await requests.ReactivateAsync(connection.Id, ct) : null;
            return new ConnectionCredentialsResult(connection.Id, connection.Name, connection.SourceCode, connection.AdapterKind, false, version, result);
        });

    /// <summary>Tests the stored credentials now (see the remarks of the class). A Disabled connection is not tested.</summary>
    public Task<ReactivationResult> TestAsync(string connectionIdOrName, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var connection = await db.InTransactionAsync(async token =>
            {
                var c = await FindAsync(connectionIdOrName, token);
                if (c.Status == ConnectionStatus.Active)
                {
                    c.MarkNeedsAttention(WaitingForTest, time.GetUtcNow());
                    await db.SaveChangesAsync(token);
                }
                return c;
            }, ct);
            return await requests.ReactivateAsync(connection.Id, ct);
        });

    /// <summary>Bank and Didox connections of the organization with the state of their credentials (no secret values).</summary>
    public Task<IReadOnlyList<ConnectionCredentialsInfo>> ListAsync(CancellationToken ct) =>
        RunAsync(() => db.InTransactionAsync<IReadOnlyList<ConnectionCredentialsInfo>>(async token =>
        {
            var connections = await db.Connections.AsNoTracking().Where(c => c.AdapterKind == AdapterKind.Bank || c.AdapterKind == AdapterKind.Edo)
                .OrderBy(c => c.Name).ToListAsync(token);
            var meta = await db.ConnectionSecrets.AsNoTracking().Where(s => s.Kind == ConnectionSecret.CredentialsKind)
                .Select(s => new { s.ConnectionId, s.Version, s.CreatedAt, s.RotatedAt }).ToDictionaryAsync(s => s.ConnectionId, token);
            var result = new List<ConnectionCredentialsInfo>(connections.Count);
            foreach (var c in connections)
            {
                meta.TryGetValue(c.Id, out var m);
                string? login = null;
                if (m is not null)
                {
                    try
                    {
                        login = MaskLogin(LoginPasswordCredentials.Parse((await secrets.GetAsync(c.Id, ConnectionSecret.CredentialsKind, token))!).Login);
                    }
                    catch (Exception ex) when (ex is AdapterAuthenticationException or System.Security.Cryptography.CryptographicException)
                    {
                        login = "(cannot be read: enter the credentials again)";
                    }
                }
                result.Add(new ConnectionCredentialsInfo(c.Id, c.Name, c.SourceCode, c.AdapterKind, c.Status, c.StatusReason, m is not null, login,
                    m?.Version, m?.CreatedAt, m?.RotatedAt, c.LastSuccessfulSyncAt));
            }
            return result;
        }, ct));

    /// <summary>Source codes of the registered adapters a connection can be created for.</summary>
    public IReadOnlyList<(string SourceCode, string Provider, AdapterKind Kind)> AvailableSources() =>
        registry.SourceCodes.Select(registry.Get).Select(a => (a.SourceCode, a.ProviderName, a.Kind))
            .OrderBy(a => a.Kind).ThenBy(a => a.SourceCode, StringComparer.Ordinal).ToList();

    /// <summary>First two characters and the length class of the login; never more.</summary>
    public static string MaskLogin(string login) =>
        login.Length <= 2 ? new string('*', login.Length) : login[..2] + new string('*', Math.Min(login.Length - 2, 8));

    private ISourceAdapter Adapter(string sourceCode)
    {
        try
        {
            return registry.Get(sourceCode.Trim());
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException($"No adapter is registered for source '{sourceCode.Trim()}'. Available: " +
                                        string.Join(", ", AvailableSources().Select(s => $"{s.SourceCode} ({s.Kind}, {s.Provider})")) + ".");
        }
    }

    private static (string Login, SecretValue Credentials) Credentials(string login, SecretValue password)
    {
        var cleanLogin = (login ?? "").Trim();
        if (cleanLogin.Length == 0) throw new ArgumentException("A login is required.");
        if (cleanLogin.Length > MaxLoginLength) throw new ArgumentException($"The login is longer than {MaxLoginLength} characters.");
        if (cleanLogin.Any(char.IsControl)) throw new ArgumentException("The login contains control characters.");
        var plain = password.Reveal();
        if (plain.Length == 0) throw new ArgumentException("A password is required.");
        if (plain.Length > MaxPasswordLength) throw new ArgumentException($"The password is longer than {MaxPasswordLength} characters.");
        return (cleanLogin, LoginPasswordCredentials.Create(cleanLogin, plain));
    }

    private async Task<Connection> FindAsync(string idOrName, CancellationToken ct)
    {
        var key = idOrName.Trim();
        var connection = Guid.TryParse(key, out var id)
            ? await db.Connections.SingleOrDefaultAsync(c => c.Id == id, ct)
            : await db.Connections.SingleOrDefaultAsync(c => c.Name == key, ct);
        if (connection is null) throw new KeyNotFoundException($"Connection '{key}' not found for this organization (list-connections shows them).");
        if (connection.AdapterKind is not (AdapterKind.Bank or AdapterKind.Edo))
            throw new InvalidOperationException($"Connection '{connection.Name}' is a {connection.AdapterKind} connection and has no bank or Didox credentials.");
        return connection;
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        if (tenant.TenantId is null) throw new InvalidOperationException("Connections are managed in a tenant context.");
        return await work();
    }
}
