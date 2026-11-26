using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Synect.Application.Adapters;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>An explicit operation of the fake bank (golden data sets): sent in the statement of its account and day.</summary>
public sealed class FakeBankOperation
{
    /// <summary>Name used by tests and the golden set; the bank id (b2_id) is derived from the position in the list.</summary>
    public required string Key { get; init; }
    public int AccountIndex { get; init; }
    public DateOnly Day { get; init; }

    /// <summary>1 = incoming, 2 = outgoing (Kapital Bank dir).</summary>
    public int Dir { get; init; } = 1;

    /// <summary>Amount in tiyin (or cents of the account currency).</summary>
    public long Amount { get; init; }

    public string? Inn { get; init; }
    public string? Name { get; init; }

    /// <summary>The counterparty's account (acc_dt of incoming, acc_ct of outgoing operations).</summary>
    public string? Account { get; init; }

    public string? Purpose { get; init; }
    public int Err { get; init; }
}

/// <summary>Controls how the fake bank behaves for one connection. Mutable on purpose (tests, demos).</summary>
public sealed class FakeBankScenario
{
    public int Seed { get; set; } = 42;
    public int AccountCount { get; set; } = 1;

    /// <summary>Currency codes (digits 6-8 of the account number, and "val") assigned to accounts in turn.</summary>
    public string[] AccountCurrencies { get; set; } = ["000"];

    /// <summary>Operations per day are drawn from [Min, Max].</summary>
    public int MinOperationsPerDay { get; set; } = 1;
    public int MaxOperationsPerDay { get; set; } = 5;

    /// <summary>When positive, counterparties are drawn from a fixed pool of this size (INN 300000000 + index).</summary>
    public int CounterpartyPoolSize { get; set; }

    /// <summary>Share of operations sent without counterparty INN (fees, cash).</summary>
    public double NoInnShare { get; set; }

    /// <summary>Explicit operations, added to the statements of their account and day.</summary>
    public List<FakeBankOperation> FixedOperations { get; } = [];

    /// <summary>Statements contain only <see cref="FixedOperations"/> (no random operations).</summary>
    public bool OnlyFixedOperations { get; set; }

    /// <summary>b2_id of the fixed operation with this key.</summary>
    public string OperationIdOf(string key) =>
        (FixedOperationIdBase + FixedOperations.FindIndex(o => o.Key == key)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal const long FixedOperationIdBase = 9_000_000_000;

    /// <summary>Days that get one extra incoming operation (INN 309999999, 1 234.00), to simulate late changes.</summary>
    public HashSet<DateOnly> ExtraDays { get; } = [];

    /// <summary>APILogin is rejected (error in the envelope).</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Data methods return envelope error 61002 (IP not allowed).</summary>
    public bool IpBlocked { get; set; }

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

    /// <summary>The next N data calls fail with HTTP 429.</summary>
    public int RateLimitFailures;

    /// <summary>Retry-After sent with simulated 429 responses.</summary>
    public TimeSpan? RateLimitRetryAfter { get; set; }

    /// <summary>Number of APILogin calls.</summary>
    public int LoginCount;

    /// <summary>Number of data calls (including failed ones).</summary>
    public int CallCount;

    internal readonly ConcurrentDictionary<string, bool> Sessions = new();

    /// <summary>Invalidates every issued sid, as a server-side session expiry would.</summary>
    public void ExpireSessions() => Sessions.Clear();

    /// <summary>Days with no operations (still closed, zero totals).</summary>
    public HashSet<DateOnly> EmptyDays { get; } = [];

    /// <summary>Days reported as not closed yet (fin = 0).</summary>
    public HashSet<DateOnly> OpenDays { get; } = [];

    /// <summary>Days whose response repeats every operation twice.</summary>
    public HashSet<DateOnly> DuplicateDays { get; } = [];

    /// <summary>Days with one extra operation rejected by the bank (err != 0, not in the day totals).</summary>
    public HashSet<DateOnly> ErroneousDays { get; } = [];

    /// <summary>Days whose closing balance (saldo_out) of the first account is off by 1 000.00, so that the day balance does not add up.</summary>
    public HashSet<DateOnly> BrokenBalanceDays { get; } = [];
}

/// <summary>
/// Produces deterministic Kapital Bank shaped JSON (envelope { error, id, result }). The same
/// (seed, account, day) always yields the same operations with the same b2_id, which models
/// the bank re-sending operations.
/// </summary>
public sealed class FakeBankDataSource
{
    public const int ErroneousOperationErrorCode = 5;
    public const string ErroneousOperationMessage = "Insufficient funds";

    private readonly ConcurrentDictionary<Guid, FakeBankScenario> _scenarios = new();
    private long _requestId;

    public FakeBankScenario For(Guid connectionId) => _scenarios.GetOrAdd(connectionId, _ => new FakeBankScenario());

    public static string AccountNumber(int seed, int index, string currency = "000") =>
        $"20208{currency}{seed % 1000:D3}{index + 1:D6}001";

    private static string CurrencyOf(FakeBankScenario s, int index) => s.AccountCurrencies[index % s.AccountCurrencies.Length];

    /// <summary>APILogin response. The password is only checked for presence.</summary>
    public string LoginJson(Guid connectionId, string login, string password)
    {
        var s = For(connectionId);
        Interlocked.Increment(ref s.LoginCount);
        if (s.AuthFailure || string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            return ErrorEnvelope(10001, "Invalid login or password");
        var sid = "sid-" + Guid.NewGuid().ToString("N");
        s.Sessions[sid] = true;
        // Shape from the documentation description: sid, login and the organisations with their accounts.
        var accounts = new JsonArray();
        for (var i = 0; i < s.AccountCount; i++)
            accounts.Add(new JsonObject { ["account"] = AccountNumber(s.Seed, i, CurrencyOf(s, i)), ["val"] = CurrencyOf(s, i), ["s_out"] = 1_000_000_000_00L });
        return Envelope(new JsonObject
        {
            ["sid"] = sid,
            ["login"] = login,
            ["clients"] = new JsonArray(new JsonObject
            {
                ["id"] = (s.Seed * 100).ToString(),
                ["branch"] = "00974",
                ["accounts"] = accounts,
            }),
        });
    }

    /// <summary>GetAccounts / GetAcc1C response.</summary>
    public async Task<string> AccountsJsonAsync(Guid connectionId, string sid, CancellationToken ct)
    {
        var s = For(connectionId);
        if (await GateAsync(s, sid, ct) is { } error) return error;

        var items = new JsonArray();
        for (var i = 0; i < s.AccountCount; i++)
        {
            items.Add(new JsonObject
            {
                ["aid"] = s.Seed * 100 + i,
                ["branch"] = "00974",
                ["account"] = AccountNumber(s.Seed, i, CurrencyOf(s, i)),
                ["name"] = FakeNames.Client(s.Seed),
                ["val"] = CurrencyOf(s, i),
                ["o_date"] = "2024-01-15",
                ["l_date"] = "2026-11-06",
                ["s_in"] = 1_000_000_000_00L,
                ["s_out"] = 1_000_000_000_00L,
                ["dt"] = 0,
                ["ct"] = 0,
                ["canpay"] = 1,
                ["state"] = 1,
                ["notif_in"] = 0,
                ["notif_out"] = 0,
            });
        }
        return Envelope(items);
    }

    /// <summary>GetDoc1C response for one account and day.</summary>
    public async Task<string> StatementJsonAsync(Guid connectionId, string sid, string account, DateOnly day, CancellationToken ct)
    {
        var s = For(connectionId);
        if (await GateAsync(s, sid, ct) is { } error) return error;

        var accountIndex = int.Parse(account.Substring(11, 6)) - 1;
        var currency = account.Substring(5, 3);
        // Names and accounts come from their own generator, so that the amounts (and thus every opening balance) can be
        // replayed cheaply without building the operations.
        var random = new Random(unchecked(s.Seed * 7919 + day.DayNumber * 31 + accountIndex + 1_000_003));
        var opening = OpeningBalance(s, accountIndex, currency, day);

        var content = new JsonArray();
        long debit = 0, credit = 0;
        var draws = Draws(s, accountIndex, currency, day, opening);
        for (var i = 0; i < draws.Count; i++)
        {
            var (dir, amount, noInn) = draws[i];
            if (dir == 1) credit += amount; else debit += amount;
            var op = Operation(s, account, day, accountIndex, i, dir, amount, random, err: 0, noInn);
            content.Add(op);
            if (s.DuplicateDays.Contains(day)) content.Add(op.DeepClone());
        }

        for (var k = 0; k < s.FixedOperations.Count; k++)
        {
            var f = s.FixedOperations[k];
            if (f.Day != day || f.AccountIndex != accountIndex) continue;
            var op = Operation(s, account, day, accountIndex, 0, f.Dir, f.Amount, random, f.Err);
            var side = f.Dir == 1 ? "dt" : "ct";
            op["b2_id"] = FakeBankScenario.FixedOperationIdBase + k;
            op["inn_" + side] = f.Inn ?? "";
            op["name_" + side] = f.Name ?? (string.IsNullOrEmpty(f.Inn) ? "BANK" : "PARTNER " + f.Inn);
            op["acc_" + side] = f.Account ?? $"20208000{k:D9}001";
            op["purpose"] = f.Purpose ?? "";
            op["num"] = (5000 + k).ToString(System.Globalization.CultureInfo.InvariantCulture);
            content.Add(op);
            if (f.Err != 0) continue; // rejected by the bank: not in the day totals
            if (f.Dir == 1) credit += f.Amount; else debit += f.Amount;
        }

        if (s.ExtraDays.Contains(day))
        {
            var extra = Operation(s, account, day, accountIndex, 800, 1, 1_234_00, random, err: 0);
            extra["inn_dt"] = "309999999";
            extra["name_dt"] = "OOO \"LATE PAYER\"";
            content.Add(extra);
            credit += 1_234_00;
        }

        if (s.ErroneousDays.Contains(day))
        {
            // Rejected payment: present in the statement, absent from the totals.
            content.Add(Operation(s, account, day, accountIndex, 900, 2, 777_00, random, ErroneousOperationErrorCode));
        }

        var saldoIn = opening;
        var result = new JsonObject
        {
            ["content"] = content,
            ["total_debit"] = debit,
            ["total_credit"] = credit,
            ["saldo_in"] = saldoIn,
            ["saldo_out"] = saldoIn + credit - debit + (accountIndex == 0 && s.BrokenBalanceDays.Contains(day) ? 1_000_00L : 0L),
            ["fin"] = s.OpenDays.Contains(day) ? 0 : 1,
            ["oper_day"] = day.ToString("yyyy-MM-dd"),
        };
        return Envelope(result);
    }

    /// <summary>The first day of the fake account history; earlier days open with the start balance.</summary>
    public static readonly DateOnly HistoryStart = new(2024, 1, 15);

    private static bool IsForeign(string currency) => currency is not ("000" or "860");

    /// <summary>Opening balance of a new account: 1 bn UZS, 150 000 in a foreign currency (minor units).</summary>
    public static long StartBalance(string currency) => IsForeign(currency) ? 150_000_00L : 1_000_000_000_00L;

    /// <summary>
    /// The random operations of a day: direction, amount, and whether it is a bank fee without counterparty INN.
    /// Amounts fit the currency (UZS 1 000 - 50 m, foreign 100 - 40 000; fees 5 000 - 300 000 UZS or 5 - 60);
    /// incoming operations are likelier while the balance is below the start balance and an outgoing one never
    /// overdraws the account, so the balance stays plausible over years.
    /// </summary>
    private static List<(int Dir, long Amount, bool NoInn)> Draws(FakeBankScenario s, int accountIndex, string currency, DateOnly day, long opening)
    {
        if (s.EmptyDays.Contains(day) || s.OnlyFixedOperations) return [];
        var r = new Random(unchecked(s.Seed * 7919 + day.DayNumber * 31 + accountIndex));
        var count = r.Next(s.MinOperationsPerDay, s.MaxOperationsPerDay + 1);
        var foreign = IsForeign(currency);
        var target = StartBalance(currency);
        var balance = opening;
        var list = new List<(int, long, bool)>(count);
        for (var i = 0; i < count; i++)
        {
            var noInn = s.NoInnShare > 0 && r.NextDouble() < s.NoInnShare;
            int dir;
            long amount;
            if (noInn)
            {
                dir = 2;
                amount = (foreign ? r.Next(5, 61) : r.Next(5_000, 300_001)) * 100L;
            }
            else
            {
                dir = r.NextDouble() < (balance < target ? 0.6 : 0.4) ? 1 : 2;
                amount = (foreign ? r.Next(100, 40_001) : r.Next(1_000, 50_000_000)) * 100L;
            }
            if (dir == 2 && amount > balance) dir = 1;
            balance += dir == 1 ? amount : -amount;
            list.Add((dir, amount, noInn));
        }
        return list;
    }

    /// <summary>Net of everything a day adds to the balance (random, fixed and extra operations; rejected ones do not count).</summary>
    private static long DayNet(FakeBankScenario s, int accountIndex, string currency, DateOnly day, long opening)
    {
        var net = Draws(s, accountIndex, currency, day, opening).Sum(d => d.Dir == 1 ? d.Amount : -d.Amount);
        foreach (var f in s.FixedOperations)
            if (f.Day == day && f.AccountIndex == accountIndex && f.Err == 0) net += f.Dir == 1 ? f.Amount : -f.Amount;
        if (s.ExtraDays.Contains(day)) net += 1_234_00;
        return net;
    }

    /// <summary>Opening balance of a day = closing balance of the previous day, replayed from <see cref="HistoryStart"/>.</summary>
    private static long OpeningBalance(FakeBankScenario s, int accountIndex, string currency, DateOnly day)
    {
        var balance = StartBalance(currency);
        for (var d = HistoryStart; d < day; d = d.AddDays(1))
            balance += DayNet(s, accountIndex, currency, d, balance);
        return balance;
    }

    private static JsonObject Operation(
        FakeBankScenario s, string account, DateOnly day, int accountIndex, int i, int dir, long amount, Random random, int err, bool noInn = false)
    {
        var partnerInn = s.CounterpartyPoolSize > 0
            ? (300_000_000 + random.Next(0, s.CounterpartyPoolSize)).ToString()
            : (300_000_000 + random.Next(0, 99_999_999)).ToString();
        if (noInn) partnerInn = "";
        var partnerAccount = $"20208000{random.Next(0, 999_999_999):D9}001";
        var partnerName = noInn ? "BANK FEE" : FakeNames.Partner(partnerInn);
        var ourInn = (200_000_000 + s.Seed).ToString();
        var ourName = FakeNames.Client(s.Seed);
        var b2Id = $"{day.DayNumber}{accountIndex:D2}{i:D3}";

        return new JsonObject
        {
            ["b2_id"] = long.Parse(b2Id),
            ["general_id"] = long.Parse(b2Id) + 5_000_000_000,
            ["uniq"] = $"U{b2Id}",
            ["num"] = (1000 + i).ToString(),
            ["ddate"] = day.ToString("yyyy-MM-dd"),
            ["vdate"] = day.ToString("yyyy-MM-dd"),
            ["mfo_dt"] = dir == 1 ? "00444" : "00974",
            ["acc_dt"] = dir == 1 ? partnerAccount : account,
            ["name_dt"] = dir == 1 ? partnerName : ourName,
            ["inn_dt"] = dir == 1 ? partnerInn : ourInn,
            ["mfo_ct"] = dir == 1 ? "00974" : "00444",
            ["acc_ct"] = dir == 1 ? account : partnerAccount,
            ["name_ct"] = dir == 1 ? ourName : partnerName,
            ["inn_ct"] = dir == 1 ? ourInn : partnerInn,
            ["purpose"] = noInn ? "Bank fee for account maintenance"
                : dir == 1 ? $"Payment for goods under contract #{i + 10}" : $"Payment for services, invoice #{i + 20}",
            ["purp_code"] = dir == 1 ? "00668" : "00702",
            ["amount"] = amount,
            ["dtype"] = "01",
            ["state"] = err == 0 ? 3 : 9,
            ["dir"] = dir,
            ["err"] = err,
            ["err_msg"] = err == 0 ? "" : ErroneousOperationMessage,
        };
    }

    private string Envelope(JsonNode result) => new JsonObject
    {
        ["error"] = null,
        ["id"] = Interlocked.Increment(ref _requestId),
        ["result"] = result,
    }.ToJsonString();

    /// <summary>
    /// Simulates the transport in front of every data method: hangs, HTTP 503/429/401, IP restriction.
    /// Returns an envelope error to send instead of data, or null to continue.
    /// </summary>
    private async Task<string?> GateAsync(FakeBankScenario s, string sid, CancellationToken ct)
    {
        Interlocked.Increment(ref s.CallCount);
        await s.WaitWhileHangingAsync(ct);
        if (TryTake(ref s.TransientFailures)) throw new AdapterTransientException("HTTP 503 Service Unavailable (fake bank).");
        if (TryTake(ref s.RateLimitFailures)) throw new AdapterRateLimitException("HTTP 429 Too Many Requests (fake bank).", s.RateLimitRetryAfter);
        if (!s.Sessions.ContainsKey(sid)) throw new AdapterAuthenticationException("HTTP 401: session expired (fake bank).");
        return s.IpBlocked ? ErrorEnvelope(61002, "IP address is not allowed") : null;
    }

    private static bool TryTake(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return true;
        }
    }

    private string ErrorEnvelope(int code, string message) => new JsonObject
    {
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        ["id"] = Interlocked.Increment(ref _requestId),
        ["result"] = null,
    }.ToJsonString();
}
