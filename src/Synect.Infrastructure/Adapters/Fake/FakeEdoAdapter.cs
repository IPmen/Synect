using Synect.Application.Adapters;
using Synect.Domain.Edo;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Didox;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>EDO adapter backed by <see cref="FakeEdoDataSource"/> and the real <see cref="DidoxMapper"/>.</summary>
public sealed class FakeEdoAdapter(FakeEdoDataSource source, DidoxMapper mapper) : IEdoAdapter
{
    public const string Code = "fake_didox";

    public string SourceCode => Code;
    public AdapterKind Kind => AdapterKind.Edo;

    // Small page size so that paging is exercised with the default fake data set.
    public EdoAdapterCapabilities Capabilities { get; } = new(SupportsUpdatedSince: true, PageSize: 5);

    public Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            source.EnsureAuthorized(context.ConnectionId);
            return Task.FromResult(ConnectionTestResult.Ok());
        }
        catch (AdapterAuthenticationException ex)
        {
            return Task.FromResult(ConnectionTestResult.Failed(ex.Message));
        }
    }

    public Task<EdoDocumentPage> ListDocumentsAsync(
        AdapterContext context, EdoDocumentOwner owner, DateTimeOffset? updatedSince, int page, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        source.EnsureAuthorized(context.ConnectionId);
        var json = source.ListJson(context.ConnectionId, owner, updatedSince, page, Capabilities.PageSize);
        var list = mapper.MapDocumentList(json);
        var hasMore = list.Total is { } total
            ? page * Capabilities.PageSize < total
            : list.Items.Count == Capabilities.PageSize;
        return Task.FromResult(new EdoDocumentPage(list.Items, page, hasMore));
    }

    public Task<EdoDocumentInfo?> GetDocumentAsync(AdapterContext context, string externalId, CancellationToken ct)
    {
        source.EnsureAuthorized(context.ConnectionId);
        var json = source.DocumentJson(context.ConnectionId, externalId);
        return Task.FromResult(json is null ? null : mapper.MapSingleDocument(json));
    }
}
