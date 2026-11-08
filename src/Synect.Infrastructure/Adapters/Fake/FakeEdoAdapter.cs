using Synect.Application.Adapters;
using Synect.Domain.Edo;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Didox;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// EDO adapter backed by <see cref="FakeEdoDataSource"/>, the real <see cref="DidoxMapper"/> and the real
/// <see cref="DidoxTokenCache"/> (token reused for its lifetime, refreshed ahead of expiry).
/// </summary>
public sealed class FakeEdoAdapter(FakeEdoDataSource source, DidoxMapper mapper, DidoxTokenCache tokens) : IEdoAdapter
{
    public const string Code = "fake_didox";
    public const string Provider = "Didox";

    public string SourceCode => Code;
    public string ProviderName => Provider;
    public AdapterKind Kind => AdapterKind.Edo;

    // Small page size so that paging is exercised with the default fake data set.
    public EdoAdapterCapabilities Capabilities { get; } = new(SupportsUpdatedSince: true, PageSize: 5);

    public async Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            await ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, ct);
            return ConnectionTestResult.Ok();
        }
        catch (AdapterException ex) when (ex is not AdapterTransientException)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async Task<EdoDocumentPage> ListDocumentsAsync(
        AdapterContext context, EdoDocumentOwner owner, DateTimeOffset? updatedSince, int page, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        await source.GateAsync(context.ConnectionId, await TokenAsync(context, ct), ct);
        var pageSize = source.For(context.ConnectionId).PageSize;
        var json = source.ListJson(context.ConnectionId, owner, updatedSince, page, pageSize);
        var list = mapper.MapDocumentList(json);
        var hasMore = list.Total is { } total
            ? page * pageSize < total
            : list.Items.Count == pageSize;
        return new EdoDocumentPage(list.Items, page, hasMore);
    }

    public async Task<EdoDocumentInfo?> GetDocumentAsync(AdapterContext context, string externalId, CancellationToken ct)
    {
        await source.GateAsync(context.ConnectionId, await TokenAsync(context, ct), ct);
        var json = source.DocumentJson(context.ConnectionId, externalId);
        return json is null ? null : mapper.MapSingleDocument(json);
    }

    private Task<string> TokenAsync(AdapterContext context, CancellationToken ct) =>
        tokens.GetTokenAsync(context.ConnectionId, async token =>
        {
            var credentials = LoginPasswordCredentials.Parse(await context.Credentials.GetCredentialsAsync(context, token));
            return DidoxTokenCache.ParseLoginResponse(
                source.LoginJson(context.ConnectionId, credentials.Login, credentials.Password.Reveal())).Token;
        }, ct);
}
