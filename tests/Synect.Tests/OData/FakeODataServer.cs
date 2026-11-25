using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synect.Tests.OData;

/// <summary>
/// A stand-in for the standard OData interface of a 1C:Fresh application, inside the test process (no network), built on
/// the documented examples (clobus.uz/articles/data_odata): answers {"odata.metadata": "...", "value": [...]}, odata.count
/// as a STRING with $inlinecount=allpages, /$count as a plain number, records with Ref_Key, DataVersion, DeletionMark and
/// references as *_Key, Basic authentication, Cyrillic resource names in the URL (percent-encoded UTF-8). Supports the
/// subset of $filter the connector sends (eq/ge with guid'…', datetime'…', '…', true/false; and/or; parentheses), $select,
/// $orderby, $top and $skip. Every request is recorded. All data is fictitious.
/// </summary>
public sealed partial class FakeODataServer(int application = 100001) : HttpMessageHandler
{
    private static int _next = 200000;

    /// <summary>A server with an application number of its own (several servers behind one <see cref="FakeODataRouter"/>).</summary>
    public static FakeODataServer CreateUnique() => new(Interlocked.Increment(ref _next));

    public string BaseUrl { get; } = $"https://clobus.uz/a/test_app/{application}";
    public string ServicePath { get; } = $"/a/test_app/{application}/odata/standard.odata/";

    public string Login { get; set; } = "odata-user";
    public string Password { get; set; } = "pw-" + Guid.NewGuid().ToString("N");

    /// <summary>403 for every authenticated request (user without rights).</summary>
    public bool Forbidden { get; set; }

    /// <summary>A server that ignores $skip (always the first page): the connector must not loop.</summary>
    public bool IgnoreSkip { get; set; }

    public ConcurrentDictionary<string, List<JsonObject>> Resources { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<(HttpMethod Method, string Resource, string Query, string RawUrl, string? Authorization)> Requests { get; } = new();

    /// <summary>Answers to give before the normal behaviour, in order (e.g. 503, 429).</summary>
    public ConcurrentQueue<Func<HttpResponseMessage>> Overrides { get; } = new();

    /// <summary>Delay before answering (timeouts).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>A server cap on $top that it applies silently (fewer records than asked, without saying so).</summary>
    public int? MaxPageSize { get; set; }

    /// <summary>$inlinecount=allpages is ignored: no odata.count in the answer.</summary>
    public bool OmitInlineCount { get; set; }

    /// <summary>/$count answers 400 (not available).</summary>
    public bool CountUnavailable { get; set; }

    /// <summary>A forced answer for matching requests (resource, decoded query); null = normal behaviour.</summary>
    public Func<string, string, HttpResponseMessage?>? FailWhen { get; set; }

    /// <summary>Called before each request is answered (resource, decoded query): to change data between pages.</summary>
    public Action<string, string>? OnRequest { get; set; }

    private long _version;

    public string NextVersion() => Convert.ToBase64String(BitConverter.GetBytes(Interlocked.Increment(ref _version)).Reverse().ToArray());

    public JsonObject Add(string resource, JsonObject record)
    {
        record["Ref_Key"] ??= Guid.NewGuid().ToString("D");
        record["DataVersion"] = NextVersion();
        record["DeletionMark"] ??= false;
        Resources.GetOrAdd(resource, _ => []).Add(record);
        return record;
    }

    /// <summary>Changes a record the way 1C does on save: new DataVersion.</summary>
    public void Update(string resource, string refKey, Action<JsonObject> change)
    {
        var r = Find(resource, refKey);
        change(r);
        r["DataVersion"] = NextVersion();
    }

    public JsonObject Find(string resource, string refKey) => Resources[resource].Single(x => (string)x["Ref_Key"]! == refKey);

    public void Remove(string resource, string refKey) => Resources[resource].RemoveAll(x => (string)x["Ref_Key"]! == refKey);

    public int Count(Func<(HttpMethod Method, string Resource, string Query, string RawUrl, string? Authorization), bool> predicate) => Requests.Count(predicate);

    /// <summary>For <see cref="FakeODataRouter"/>.</summary>
    public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var query = ParseQuery(uri.Query);
        var resource = path.StartsWith(ServicePath, StringComparison.Ordinal) ? path[ServicePath.Length..] : path;
        Requests.Enqueue((request.Method, resource, Uri.UnescapeDataString(uri.Query), uri.AbsoluteUri, request.Headers.Authorization?.ToString()));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Overrides.TryDequeue(out var forced)) return forced();
        OnRequest?.Invoke(resource, Uri.UnescapeDataString(uri.Query));
        if (FailWhen?.Invoke(resource, Uri.UnescapeDataString(uri.Query)) is { } failure) return failure;
        if (request.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Login + ":" + Password));
        if (request.Headers.Authorization?.ToString() != expected) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (Forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (!path.StartsWith(ServicePath, StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (resource == "$metadata") return Text(Metadata(), "application/xml");
        if (resource == "") return Json(new JsonObject { ["value"] = new JsonArray(Resources.Keys.Select(k => (JsonNode)new JsonObject { ["name"] = k, ["url"] = k }).ToArray()) });

        var isCount = resource.EndsWith("/$count", StringComparison.Ordinal);
        if (isCount) resource = resource[..^"/$count".Length];
        string? key = null;
        var m = KeyPattern().Match(resource);
        if (m.Success)
        {
            resource = m.Groups[1].Value;
            key = m.Groups[2].Value;
        }
        if (!Resources.TryGetValue(resource, out var all)) return new HttpResponseMessage(HttpStatusCode.NotFound);

        IEnumerable<JsonObject> rows = all;
        if (key is not null) rows = rows.Where(r => (string)r["Ref_Key"]! == key);
        if (query.TryGetValue("$filter", out var filter))
        {
            try { rows = rows.Where(r => Eval(filter, r)).ToList(); }
            catch (FormatException) { return new HttpResponseMessage(HttpStatusCode.BadRequest); }
        }
        var list = rows.ToList();
        if (isCount) return CountUnavailable ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Text(list.Count.ToString(CultureInfo.InvariantCulture), "text/plain");

        if (query.TryGetValue("$orderby", out var order))
        {
            var prop = order.Split(' ')[0];
            list = list.OrderBy(r => r[prop]?.ToString(), StringComparer.Ordinal).ToList();
        }
        var total = list.Count;
        if (!IgnoreSkip && query.TryGetValue("$skip", out var skip)) list = list.Skip(int.Parse(skip, CultureInfo.InvariantCulture)).ToList();
        if (query.TryGetValue("$top", out var top)) list = list.Take(Math.Min(int.Parse(top, CultureInfo.InvariantCulture), MaxPageSize ?? int.MaxValue)).ToList();
        var select = query.TryGetValue("$select", out var s) ? s.Split(',') : null;
        var value = new JsonArray(list.Select(r => (JsonNode)Project(r, select)).ToArray());
        var body = new JsonObject { ["odata.metadata"] = $"https://clobus.uz:443{ServicePath}$metadata#{resource}", ["value"] = value };
        if (!OmitInlineCount && query.TryGetValue("$inlinecount", out var ic) && ic == "allpages") body["odata.count"] = total.ToString(CultureInfo.InvariantCulture);
        return Json(body);
    }

    [GeneratedRegex(@"^(.+)\(guid'([0-9a-fA-F-]{36})'\)$")]
    private static partial Regex KeyPattern();

    private static JsonObject Project(JsonObject r, string[]? select)
    {
        if (select is null) return (JsonObject)r.DeepClone();
        var o = new JsonObject();
        foreach (var p in select) if (r.TryGetPropertyValue(p, out var v)) o[p] = v?.DeepClone();
        return o;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) continue;
            result[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return result;
    }

    // ---- a tiny $filter evaluator: (a) and (b), x or y, Prop eq|ne|ge|gt|le|lt literal ----

    private static bool Eval(string filter, JsonObject r)
    {
        filter = filter.Trim();
        var andParts = SplitTop(filter, " and ");
        if (andParts.Count > 1) return andParts.All(p => Eval(p, r));
        var orParts = SplitTop(filter, " or ");
        if (orParts.Count > 1) return orParts.Any(p => Eval(p, r));
        if (filter.StartsWith('(') && filter.EndsWith(')')) return Eval(filter[1..^1], r);
        var m = AtomPattern().Match(filter);
        if (!m.Success) throw new FormatException(filter);
        var (prop, op, literal) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        var actual = r[prop];
        int cmp;
        if (literal.StartsWith("guid'", StringComparison.Ordinal))
            cmp = string.Compare(actual?.ToString(), literal[5..^1], StringComparison.OrdinalIgnoreCase);
        else if (literal.StartsWith("datetime'", StringComparison.Ordinal))
            cmp = DateTime.Parse(actual!.ToString(), CultureInfo.InvariantCulture).CompareTo(DateTime.Parse(literal[9..^1], CultureInfo.InvariantCulture));
        else if (literal.StartsWith('\''))
            cmp = string.Compare(actual?.ToString(), literal[1..^1].Replace("''", "'", StringComparison.Ordinal), StringComparison.Ordinal);
        else if (literal is "true" or "false")
            cmp = (actual?.GetValue<bool>() ?? false) == (literal == "true") ? 0 : 1;
        else throw new FormatException(filter);
        return op switch { "eq" => cmp == 0, "ne" => cmp != 0, "ge" => cmp >= 0, "gt" => cmp > 0, "le" => cmp <= 0, "lt" => cmp < 0, _ => false };
    }

    [GeneratedRegex(@"^([\p{L}\p{N}_]+) (eq|ne|ge|gt|le|lt) (.+)$")]
    private static partial Regex AtomPattern();

    private static List<string> SplitTop(string s, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') inQuote = !inQuote;
            else if (!inQuote && c == '(') depth++;
            else if (!inQuote && c == ')') depth--;
            else if (!inQuote && depth == 0 && string.CompareOrdinal(s, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(s[start..i]);
                i += separator.Length - 1;
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private string Metadata()
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><edmx:Edmx xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx" Version="1.0"><edmx:DataServices><Schema xmlns="http://schemas.microsoft.com/ado/2009/11/edm" Namespace="StandardODATA">""");
        foreach (var r in Resources.Keys) sb.Append($"""<EntityType Name="{r}"><Key><PropertyRef Name="Ref_Key"/></Key><Property Name="Ref_Key" Type="Edm.Guid" Nullable="false"/><Property Name="DataVersion" Type="Edm.String"/></EntityType>""");
        sb.Append("""<EntityContainer Name="EnterpriseV8" m:IsDefaultEntityContainer="true" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">""");
        foreach (var r in Resources.Keys) sb.Append($"""<EntitySet Name="{r}" EntityType="StandardODATA.{r}"/>""");
        sb.Append("</EntityContainer></Schema></edmx:DataServices></edmx:Edmx>");
        return sb.ToString();
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body, string type) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    public static HttpResponseMessage Status(HttpStatusCode code, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(code);
        if (retryAfter is { } ra) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
        return r;
    }

    // ---- fictitious data in the shape of the starting profile (clobus-bp-uz) ----

    public const string Uzs = "a1111111-0000-0000-0000-000000000860";
    public const string Usd = "a1111111-0000-0000-0000-000000000840";
    public const string Bank = "b2222222-0000-0000-0000-000000000001";
    public const string Org = "c3333333-0000-0000-0000-000000000001";

    /// <summary>Two currencies, a bank, three counterparties, a contract, an own bank account, a sale, a purchase, a receipt.</summary>
    public void SeedSample(DateOnly today)
    {
        Add("Catalog_Валюты", new JsonObject { ["Ref_Key"] = Uzs, ["Code"] = "860", ["Description"] = "сўм" });
        Add("Catalog_Валюты", new JsonObject { ["Ref_Key"] = Usd, ["Code"] = "840", ["Description"] = "USD" });
        Add("Catalog_Банки", new JsonObject { ["Ref_Key"] = Bank, ["Code"] = "00014", ["Description"] = "Test Bank" });
        Add("Catalog_Номенклатура", new JsonObject { ["Ref_Key"] = Item, ["Description"] = "Товар А", ["Артикул"] = "06401001001000000" });
        Add("ChartOfAccounts_Хозрасчетный", new JsonObject { ["Ref_Key"] = Gl, ["Code"] = "5110" });
        Add("Catalog_СтатьиДвиженияДенежныхСредств", new JsonObject { ["Ref_Key"] = CashFlow, ["Description"] = "Оплата от покупателей" });
        Add("Catalog_Контрагенты", Counterparty(CpA, "ООО «Альфа»", "301000001", "ЮридическоеЛицо"));
        Add("Catalog_Контрагенты", Counterparty(CpB, "Бета МЧЖ", "301000002", "ЮридическоеЛицо"));
        Add("Catalog_Контрагенты", Counterparty(CpC, "Иванов И.И.", "41234567890123", "ФизическоеЛицо"));
        Add("Catalog_Контрагенты", new JsonObject { ["Ref_Key"] = Guid.NewGuid().ToString("D"), ["IsFolder"] = true, ["Description"] = "Группа" });
        Add("Catalog_ДоговорыКонтрагентов", new JsonObject
        {
            ["Ref_Key"] = Contract, ["Owner_Key"] = CpA, ["IsFolder"] = false, ["Description"] = "Договор 7", ["Номер"] = "7", ["Дата"] = "2026-01-10T00:00:00",
            ["ВидДоговора"] = "СПокупателем", ["ВалютаВзаиморасчетов_Key"] = Uzs, ["СрокДействия"] = "0001-01-01T00:00:00", ["СрокОплаты"] = 30,
            ["Организация_Key"] = Org,
        });
        Add("Catalog_БанковскиеСчета", new JsonObject
        {
            ["Ref_Key"] = Account, ["Owner"] = Org, ["Owner_Type"] = "StandardODATA.Catalog_Организации", ["НомерСчета"] = "20208000900000000001",
            ["Банк_Key"] = Bank, ["ВалютаДенежныхСредств_Key"] = Uzs, ["СчетБанк_Key"] = Gl, ["Description"] = "Основной",
        });
        Add("Catalog_БанковскиеСчета", new JsonObject
        {
            ["Ref_Key"] = Guid.NewGuid().ToString("D"), ["Owner"] = CpA, ["Owner_Type"] = "StandardODATA.Catalog_Контрагенты", ["НомерСчета"] = "20208000900000000999",
            ["Банк_Key"] = Bank, ["ВалютаДенежныхСредств_Key"] = Uzs,
        });
        Add("Document_РеализацияТоваровУслуг", Sale(Sale1, "0001", today.AddDays(-3), 1_120_000m));
        Add("Document_ПоступлениеТоваровУслуг", new JsonObject
        {
            ["Ref_Key"] = Purchase1, ["Number"] = "П-0001", ["Date"] = $"{today.AddDays(-100):yyyy-MM-dd}T09:00:00", ["Posted"] = true, ["Контрагент_Key"] = CpB,
            ["ДоговорКонтрагента_Key"] = ODataNull, ["ВалютаДокумента_Key"] = Usd, ["СуммаДокумента"] = 500.5m, ["Комментарий"] = "",
            ["СуммаВключаетНДС"] = false, ["Организация_Key"] = Org,
            ["Товары"] = new JsonArray(new JsonObject { ["LineNumber"] = "1", ["Номенклатура_Key"] = Item, ["Количество"] = 1, ["Цена"] = 446.88m, ["Сумма"] = 446.88m, ["СуммаНДС"] = 53.62m, ["СтавкаНДС"] = "НДС12" }),
            ["Услуги"] = new JsonArray(),
        });
        Add("Document_ПоступлениеНаРасчетныйСчет", new JsonObject
        {
            ["Ref_Key"] = Receipt1, ["Number"] = "ПП-12", ["Date"] = $"{today.AddDays(-2):yyyy-MM-dd}T15:00:00", ["Posted"] = true, ["СчетОрганизации_Key"] = Account,
            ["Контрагент"] = CpA, ["Контрагент_Type"] = "StandardODATA.Catalog_Контрагенты", ["СуммаДокумента"] = 1_120_000, ["ВалютаДокумента_Key"] = Uzs,
            ["НазначениеПлатежа"] = "Оплата по договору 7", ["ДоговорКонтрагента_Key"] = Contract, ["НомерВходящегоДокумента"] = "12",
            ["ДатаВходящегоДокумента"] = $"{today.AddDays(-2):yyyy-MM-dd}T00:00:00", ["СтатьяДвиженияДенежныхСредств_Key"] = CashFlow, ["Организация_Key"] = Org,
            ["РасшифровкаПлатежа"] = new JsonArray(new JsonObject { ["LineNumber"] = "1", ["Сделка"] = Sale1, ["Сделка_Type"] = "StandardODATA.Document_РеализацияТоваровУслуг", ["СуммаПлатежа"] = 1_120_000 }),
        });
        Add("Document_СписаниеСРасчетногоСчета", new JsonObject
        {
            ["Ref_Key"] = Payment1, ["Number"] = "15", ["Date"] = $"{today.AddDays(-1):yyyy-MM-dd}T11:00:00", ["Posted"] = true, ["СчетОрганизации_Key"] = Account,
            ["Контрагент"] = Guid.NewGuid().ToString("D"), ["Контрагент_Type"] = "StandardODATA.Catalog_ФизическиеЛица", ["СуммаДокумента"] = 2_000_000, ["ВалютаДокумента_Key"] = Uzs,
            ["НазначениеПлатежа"] = "Зарплата", ["Организация_Key"] = Org, ["РасшифровкаПлатежа"] = new JsonArray(),
        });
    }

    public const string ODataNull = "00000000-0000-0000-0000-000000000000";
    public const string Item = "d4444444-0000-0000-0000-000000000001";
    public const string Gl = "e5555555-0000-0000-0000-000000000001";
    public const string CashFlow = "f6666666-0000-0000-0000-000000000001";
    public const string CpA = "10000000-0000-0000-0000-00000000000a";
    public const string CpB = "10000000-0000-0000-0000-00000000000b";
    public const string CpC = "10000000-0000-0000-0000-00000000000c";
    public const string Contract = "20000000-0000-0000-0000-000000000007";
    public const string Account = "30000000-0000-0000-0000-000000000001";
    public const string Sale1 = "40000000-0000-0000-0000-000000000001";
    public const string Purchase1 = "50000000-0000-0000-0000-000000000001";
    public const string Receipt1 = "60000000-0000-0000-0000-000000000001";
    public const string Payment1 = "70000000-0000-0000-0000-000000000001";

    public static JsonObject Counterparty(string id, string name, string tin, string kind) => new()
    {
        ["Ref_Key"] = id, ["IsFolder"] = false, ["Description"] = name, ["НаименованиеПолное"] = name, ["ИНН"] = tin, ["ЮридическоеФизическоеЛицо"] = kind,
    };

    public static JsonObject Sale(string id, string number, DateOnly date, decimal total) => new()
    {
        ["Ref_Key"] = id, ["Number"] = number, ["Date"] = $"{date:yyyy-MM-dd}T10:30:00", ["Posted"] = true, ["Контрагент_Key"] = CpA,
        ["ДоговорКонтрагента_Key"] = Contract, ["ВалютаДокумента_Key"] = Uzs, ["СуммаДокумента"] = total, ["Комментарий"] = "тест",
        ["СуммаВключаетНДС"] = true, ["Организация_Key"] = Org,
        ["Товары"] = new JsonArray(new JsonObject
        {
            ["LineNumber"] = "1", ["Номенклатура_Key"] = Item, ["Количество"] = 2, ["Цена"] = total / 2, ["Сумма"] = total, ["СуммаНДС"] = Math.Round(total * 12 / 112, 2),
            ["СтавкаНДС"] = "НДС12",
        }),
        ["Услуги"] = new JsonArray(new JsonObject
        {
            ["LineNumber"] = "1", ["Номенклатура_Key"] = Item, ["Содержание"] = "Доставка", ["Количество"] = 1, ["Цена"] = 0, ["Сумма"] = 0, ["СуммаНДС"] = 0,
            ["СтавкаНДС"] = "БезНДС",
        }),
    };
}

/// <summary>Routes requests to the fake server whose application path they name (one transport for a whole test host).</summary>
public sealed class FakeODataRouter : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, FakeODataServer> _servers = new(StringComparer.Ordinal);

    public FakeODataServer Add(FakeODataServer server)
    {
        _servers[server.ServicePath] = server;
        return server;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var server = _servers.FirstOrDefault(s => path.StartsWith(s.Key, StringComparison.Ordinal)).Value;
        return server is null ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) : server.HandleAsync(request, ct);
    }
}
