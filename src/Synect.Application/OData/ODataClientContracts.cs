using System.Text.Json;
using Synect.Application.Adapters;
using Synect.Application.Secrets;

namespace Synect.Application.OData;

/// <summary>One GET of a collection (or of one record by <see cref="Key"/>). Names are checked by <see cref="ODataUrl"/>.</summary>
public sealed record ODataQuery(string Resource)
{
    /// <summary>A record key, e.g. guid'…' (built with <see cref="ODataLiteral"/>); null for the collection.</summary>
    public string? Key { get; init; }

    public string? Filter { get; init; }
    public IReadOnlyList<string>? Select { get; init; }
    public string? OrderBy { get; init; }
    public int? Top { get; init; }
    public int? Skip { get; init; }

    /// <summary>$inlinecount=allpages: the answer carries odata.count, the number of records matching the filter.</summary>
    public bool InlineCount { get; init; }

    /// <summary>A virtual table of a register (Balance, Turnovers, BalanceAndTurnovers, SliceLast, SliceFirst): Resource/VirtualTable(…).</summary>
    public string? VirtualTable { get; init; }

    /// <summary>
    /// Parameters of <see cref="VirtualTable"/> as name → literal built by <see cref="ODataLiteral"/> (a quoted string such as a
    /// Condition, or datetime'…'); anything else is refused by <see cref="ODataUrl"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Parameters { get; init; }

    /// <summary>The resource as logs and messages name it (with the virtual table, never the parameters).</summary>
    public string What => VirtualTable is null ? Resource : Resource + "/" + VirtualTable;
}

/// <summary>Records of one answer (detached from the response document) and odata.count when it was asked for.</summary>
public sealed record ODataPage(IReadOnlyList<JsonElement> Items, long? Count);

/// <summary>Counters of one client (requests, retries, time), for reports and logs.</summary>
public sealed class ODataClientStats
{
    public int Requests;
    public int Retries;
    public long TotalMilliseconds;
    public long LastMilliseconds;
}

/// <summary>
/// Read-only client of one 1C application over the standard OData interface (GET only; any other method throws before
/// anything is sent). Basic authentication with the connection's login and password, which never appear in logs or
/// error messages; neither do the URL or the host of the client's application.
/// </summary>
public interface IODataClient : IDisposable
{
    ODataClientStats Stats { get; }

    /// <summary>The $metadata document (XML, CSDL) as text.</summary>
    Task<string> GetMetadataAsync(CancellationToken ct);

    /// <summary>The service document: names of the published resources.</summary>
    Task<IReadOnlyList<string>> ListResourcesAsync(CancellationToken ct);

    /// <summary>Resource/$count with an optional filter.</summary>
    Task<long> CountAsync(string resource, string? filter, CancellationToken ct);

    /// <summary>The number of records of a query (its resource or virtual table and filter): …/$count.</summary>
    Task<long> CountAsync(ODataQuery query, CancellationToken ct);

    Task<ODataPage> GetAsync(ODataQuery query, CancellationToken ct);
}

public interface IODataClientFactory
{
    /// <param name="serviceRoot">Normalized by <see cref="ODataEndpoint.Normalize"/>; checked again here.</param>
    IODataClient Create(Uri serviceRoot, string login, SecretValue password);
}

/// <summary>A method other than GET was asked of the read-only client.</summary>
public sealed class ODataMethodNotAllowedException(string method)
    : InvalidOperationException($"The OData client is read-only: HTTP {method} is not allowed, only GET.");

/// <summary>An answer that cannot be used: 400/404, a redirect, malformed JSON, a paging loop. Not retried.</summary>
public class ODataProtocolException(string message) : AdapterException(message)
{
}

/// <summary>HTTP 404: the resource is not published (not ticked on the «Состав» tab) or does not exist in the configuration.</summary>
public sealed class ODataNotPublishedException(string message) : ODataProtocolException(message)
{
}
