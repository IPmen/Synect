using System.Collections.Concurrent;
using Synect.Application.Adapters;

namespace Synect.Infrastructure.Adapters.KapitalBank;

/// <summary>
/// Caches the Kapital Bank session id (sid) per connection. A data call that fails with an
/// authentication error invalidates the sid, logs in again and is retried once.
/// </summary>
public sealed class KapitalSessionManager(KapitalBankMapper mapper)
{
    /// <summary>Sends APILogin and returns the raw JSON response. Implemented by the transport (real or fake).</summary>
    public delegate Task<string> LoginTransport(string login, string password, CancellationToken ct);

    private readonly ConcurrentDictionary<Guid, string> _sessions = new();

    public async Task<T> WithSessionAsync<T>(
        AdapterContext context, LoginTransport login, Func<string, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var sid = await GetSidAsync(context, login, forceLogin: false, ct);
        try
        {
            return await call(sid, ct);
        }
        catch (AdapterAuthenticationException)
        {
            Invalidate(context.ConnectionId);
            sid = await GetSidAsync(context, login, forceLogin: true, ct);
            return await call(sid, ct);
        }
    }

    public void Invalidate(Guid connectionId) => _sessions.TryRemove(connectionId, out _);

    public bool HasSession(Guid connectionId) => _sessions.ContainsKey(connectionId);

    private async Task<string> GetSidAsync(AdapterContext context, LoginTransport login, bool forceLogin, CancellationToken ct)
    {
        if (!forceLogin && _sessions.TryGetValue(context.ConnectionId, out var cached)) return cached;

        var credentials = LoginPasswordCredentials.Parse(await context.Credentials.GetCredentialsAsync(context, ct));
        var sid = mapper.MapLogin(await login(credentials.Login, credentials.Password.Reveal(), ct));
        _sessions[context.ConnectionId] = sid;
        return sid;
    }
}
