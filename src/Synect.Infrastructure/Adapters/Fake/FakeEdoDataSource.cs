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
}

public sealed class FakeEdoScenario
{
    internal readonly object Sync = new();
    internal List<FakeDidoxDocument>? Documents;

    public int Seed { get; set; } = 7;
    public int DocumentCount { get; set; } = 12;

    /// <summary>Every call fails with an authentication error.</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Base time of seeded documents.</summary>
    public DateTimeOffset BaseTime { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(5));
}

/// <summary>Produces Didox shaped JSON from an in-memory, mutable document set.</summary>
public sealed class FakeEdoDataSource
{
    private static readonly string[] DocTypes = ["002", "005", "007", "052"];
    private static readonly int[] Statuses = [0, 1, 2, 3, 4, 6, 8, 40];
    private static readonly TimeSpan Tashkent = TimeSpan.FromHours(5);

    private readonly ConcurrentDictionary<Guid, FakeEdoScenario> _scenarios = new();

    public FakeEdoScenario For(Guid connectionId) => _scenarios.GetOrAdd(connectionId, _ => new FakeEdoScenario());

    public void EnsureAuthorized(Guid connectionId)
    {
        if (For(connectionId).AuthFailure)
            throw new AdapterAuthenticationException("Fake Didox rejected the token (401).");
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
            var created = s.BaseTime.AddDays(i).AddMinutes(random.Next(0, 600));
            var sum = random.Next(100_000, 90_000_000) + random.Next(0, 100) / 100m;
            var vat = Math.Round(sum * 12m / 112m, 2);
            var partnerTin = (300_000_000 + random.Next(0, 99_999_999)).ToString();
            s.Documents.Add(new FakeDidoxDocument
            {
                DocId = $"{s.Seed:D2}{i:D4}{random.Next(0x10000000, int.MaxValue):x8}",
                DocType = DocTypes[i % DocTypes.Length],
                Owner = i % 2,
                Status = Statuses[random.Next(Statuses.Length)],
                Name = $"{i + 1:D4}",
                DocDate = DateOnly.FromDateTime(created.DateTime),
                PartnerTin = partnerTin,
                PartnerCompany = $"OOO \"PARTNER {partnerTin[^3..]}\"",
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

    private static JsonObject ToJson(FakeDidoxDocument d) => new()
    {
        ["doc_id"] = d.DocId,
        ["name"] = d.Name,
        ["doc_date"] = d.DocDate.ToString("yyyy-MM-dd"),
        ["doc_status"] = d.Status,
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
