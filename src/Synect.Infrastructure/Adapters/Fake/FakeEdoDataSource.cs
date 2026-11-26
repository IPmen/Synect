using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Synect.Application.Adapters;
using Synect.Domain.Edo;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>A mutable fake Didox document.</summary>
public sealed class FakeDidoxDocument
{
    public required string DocId { get; init; }
    public required string DocType { get; init; }
    public required int Owner { get; init; }
    public int Status { get; set; }
    public string? StatusComment { get; set; }
    public required string Name { get; init; }
    public DateOnly DocDate { get; init; }
    public required string PartnerTin { get; init; }
    public required string PartnerCompany { get; init; }
    public string? ContractNumber { get; init; }
    public DateOnly? ContractDate { get; init; }
    public decimal TotalSum { get; init; }
    public decimal TotalVatSum { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>Seller's account written in the document (sellerAccount); omitted from the JSON when null.</summary>
    public string? SellerAccount { get; init; }

    /// <summary>Document currency field; omitted from the JSON when null (Didox default UZS).</summary>
    public string? Currency { get; init; }
}

public sealed class FakeEdoScenario
{
    internal readonly object Sync = new();
    internal List<FakeDidoxDocument>? Documents;

    public int Seed { get; set; } = 7;
    public int DocumentCount { get; set; } = 12;

    /// <summary>Documents are spread evenly over this many days from BaseTime (default: one document per day).</summary>
    public int? SpreadDays { get; set; }

    /// <summary>When positive, partners are drawn from a fixed pool of this size (TIN 300000000 + index).</summary>
    public int CounterpartyPoolSize { get; set; }

    /// <summary>Documents per page returned by the fake list method.</summary>
    public int PageSize { get; set; } = 5;

    /// <summary>Login is rejected.</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Simulates a person logging into a company: the login response carries permissions.</summary>
    public bool PersonLogin { get; set; }

    private TaskCompletionSource _hangRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Data calls do not answer until <see cref="ReleaseHang"/> is called or the call is cancelled.</summary>
    public bool Hang
    {
        get => _hang;
        set
        {
            if (value && !_hang) _hangRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hang = value;
        }
    }

    private volatile bool _hang;

    /// <summary>Lets every hanging call continue (and stops hanging).</summary>
    public void ReleaseHang()
    {
        Hang = false;
        _hangRelease.TrySetResult();
    }

    internal Task WaitWhileHangingAsync(CancellationToken ct) => Hang ? _hangRelease.Task.WaitAsync(ct) : Task.CompletedTask;

    /// <summary>The next N data calls fail with HTTP 503.</summary>
    public int TransientFailures;

    /// <summary>Number of login calls (token requests).</summary>
    public int LoginCount;

    /// <summary>Issued tokens and their expiry (360 minutes, as in Didox).</summary>
    internal readonly ConcurrentDictionary<string, DateTimeOffset> Tokens = new();

    /// <summary>Uses exactly these documents instead of generated ones (golden data sets).</summary>
    public void UseDocuments(IEnumerable<FakeDidoxDocument> documents)
    {
        lock (Sync) Documents = documents.ToList();
    }

    /// <summary>Base time of seeded documents.</summary>
    public DateTimeOffset BaseTime { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(5));

    /// <summary>The connection's stored settings were applied (or it has none).</summary>
    internal volatile bool ConfiguredFromSettings;
}

/// <summary>Produces Didox shaped JSON from an in-memory, mutable document set.</summary>
public sealed class FakeEdoDataSource(TimeProvider time)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(360);

    private static readonly string[] DocTypes = ["002", "005", "007", "052"];
    private static readonly int[] Statuses = [0, 1, 2, 3, 4, 6, 8, 40];
    private static readonly TimeSpan Tashkent = TimeSpan.FromHours(5);

    private readonly ConcurrentDictionary<Guid, FakeEdoScenario> _scenarios = new();

    public FakeEdoScenario For(Guid connectionId) => _scenarios.GetOrAdd(connectionId, _ => new FakeEdoScenario());

    /// <summary>Applies the demo settings stored with the connection (<see cref="FakeScenarioSettings"/>) once per process.</summary>
    public void Configure(Synect.Application.Adapters.AdapterContext context)
    {
        var s = For(context.ConnectionId);
        if (s.ConfiguredFromSettings) return;
        lock (s.Sync)
        {
            if (s.ConfiguredFromSettings) return;
            if (s.Documents is null && FakeScenarioSettings.Read(context) is { } stored) FakeScenarioSettings.Apply(s, stored);
            s.ConfiguredFromSettings = true;
        }
    }

    /// <summary>Login response: { "token": "..." } or { "error": ... }. The password is only checked for presence.</summary>
    public string LoginJson(Guid connectionId, string login, string password)
    {
        var s = For(connectionId);
        Interlocked.Increment(ref s.LoginCount);
        if (s.AuthFailure || string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            return """{ "error": "invalid credentials" }""";
        var token = "tok-" + Guid.NewGuid().ToString("N");
        s.Tokens[token] = time.GetUtcNow() + TokenLifetime;
        // Shape from the documentation description; permissions only when a person logs into a company.
        var response = new JsonObject { ["token"] = token, ["related_companies"] = null, ["related_branches"] = null };
        if (s.PersonLogin)
            response["permissions"] = new JsonObject { ["tin"] = "30" + s.Seed.ToString("D7"), ["roles"] = new JsonArray("director") };
        return response.ToJsonString();
    }

    /// <summary>Simulates the transport in front of every data call: hangs, HTTP 503, HTTP 401 for bad or expired tokens.</summary>
    public async Task GateAsync(Guid connectionId, string token, CancellationToken ct)
    {
        var s = For(connectionId);
        await s.WaitWhileHangingAsync(ct);
        while (true)
        {
            var current = Volatile.Read(ref s.TransientFailures);
            if (current <= 0) break;
            if (Interlocked.CompareExchange(ref s.TransientFailures, current - 1, current) == current)
                throw new AdapterTransientException("HTTP 503 Service Unavailable (fake Didox).");
        }
        if (!s.Tokens.TryGetValue(token, out var expiresAt) || time.GetUtcNow() >= expiresAt)
            throw new AdapterAuthenticationException("HTTP 401: token is invalid or expired (fake Didox).");
    }

    public IReadOnlyList<FakeDidoxDocument> Documents(Guid connectionId)
    {
        var s = For(connectionId);
        lock (s.Sync) return [.. Seed(s)];
    }

    /// <summary>Changes a document status, as a counterparty signing or rejecting it would.</summary>
    public void SetStatus(Guid connectionId, string docId, int status, DateTimeOffset updated)
    {
        var s = For(connectionId);
        lock (s.Sync)
        {
            var doc = Seed(s).Single(d => d.DocId == docId);
            doc.Status = status;
            doc.Updated = updated;
        }
    }

    public string ListJson(Guid connectionId, EdoDocumentOwner owner, DateTimeOffset? updatedSince, int page, int pageSize)
    {
        var s = For(connectionId);
        List<FakeDidoxDocument> matching;
        lock (s.Sync)
        {
            matching = Seed(s)
                .Where(d => d.Owner == (int)owner && (updatedSince is null || d.Updated >= updatedSince))
                .OrderBy(d => d.Updated).ThenBy(d => d.DocId, StringComparer.Ordinal)
                .ToList();
        }

        var data = new JsonArray(matching.Skip((page - 1) * pageSize).Take(pageSize).Select(ToJson).ToArray<JsonNode?>());
        return new JsonObject
        {
            ["data"] = data,
            ["total"] = matching.Count,
            ["page"] = page,
            ["per_page"] = pageSize,
        }.ToJsonString();
    }

    public string? DocumentJson(Guid connectionId, string docId)
    {
        var s = For(connectionId);
        lock (s.Sync)
        {
            var doc = Seed(s).SingleOrDefault(d => d.DocId == docId);
            return doc is null ? null : new JsonObject { ["data"] = ToJson(doc) }.ToJsonString();
        }
    }

    private static List<FakeDidoxDocument> Seed(FakeEdoScenario s)
    {
        if (s.Documents is not null) return s.Documents;
        var random = new Random(s.Seed);
        s.Documents = [];
        for (var i = 0; i < s.DocumentCount; i++)
        {
            var dayOffset = s.SpreadDays is { } spread ? (double)i * spread / s.DocumentCount : i;
            var created = s.BaseTime.AddDays(dayOffset).AddMinutes(random.Next(0, 600));
            var sum = random.Next(100_000, 90_000_000) + random.Next(0, 100) / 100m;
            var vat = Math.Round(sum * 12m / 112m, 2);
            var partnerTin = s.CounterpartyPoolSize > 0
                ? (300_000_000 + random.Next(0, s.CounterpartyPoolSize)).ToString()
                : (300_000_000 + random.Next(0, 99_999_999)).ToString();
            s.Documents.Add(new FakeDidoxDocument
            {
                DocId = $"{s.Seed:D2}{i:D4}{random.Next(0x10000000, int.MaxValue):x8}",
                DocType = DocTypes[i % DocTypes.Length],
                Owner = i % 2,
                Status = Statuses[random.Next(Statuses.Length)],
                Name = $"{i + 1:D4}",
                DocDate = DateOnly.FromDateTime(created.DateTime),
                PartnerTin = partnerTin,
                PartnerCompany = FakeNames.Partner(partnerTin),
                ContractNumber = i % 3 == 0 ? null : $"C-{2026}-{i:D3}",
                ContractDate = i % 3 == 0 ? null : DateOnly.FromDateTime(s.BaseTime.AddMonths(-2).DateTime),
                TotalSum = sum,
                TotalVatSum = vat,
                Created = created,
                Updated = created.AddHours(random.Next(1, 48)),
            });
        }
        return s.Documents;
    }

    private static JsonObject ToJson(FakeDidoxDocument d)
    {
        var json = BaseJson(d);
        if (d.SellerAccount is not null) json["sellerAccount"] = d.SellerAccount;
        if (d.Currency is not null) json["currency"] = d.Currency;
        return json;
    }

    private static JsonObject BaseJson(FakeDidoxDocument d) => new()
    {
        ["doc_id"] = d.DocId,
        ["name"] = d.Name,
        ["doc_date"] = d.DocDate.ToString("yyyy-MM-dd"),
        ["doc_status"] = d.Status,
        ["status_comment"] = d.Status == 4 ? d.StatusComment ?? "Rejected: amounts do not match the contract" : d.StatusComment,
        ["doctype"] = d.DocType,
        ["owner"] = d.Owner,
        ["partnerTin"] = d.PartnerTin,
        ["partnerCompany"] = d.PartnerCompany,
        ["contract_number"] = d.ContractNumber,
        ["contract_date"] = d.ContractDate?.ToString("yyyy-MM-dd"),
        ["total_sum"] = d.TotalSum,
        ["total_vat_sum"] = d.TotalVatSum,
        ["total_delivery_sum"] = d.TotalSum - d.TotalVatSum,
        ["total_delivery_sum_with_vat"] = d.TotalSum,
        ["created"] = LocalTime(d.Created),
        ["updated"] = LocalTime(d.Updated),
    };

    private static string LocalTime(DateTimeOffset t) =>
        t.ToOffset(Tashkent).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
