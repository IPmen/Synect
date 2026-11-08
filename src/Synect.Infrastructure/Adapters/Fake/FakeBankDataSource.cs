using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Synect.Application.Adapters;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>Controls how the fake bank behaves for one connection. Mutable on purpose (tests, demos).</summary>
public sealed class FakeBankScenario
{
    public int Seed { get; set; } = 42;
    public int AccountCount { get; set; } = 1;

    /// <summary>APILogin is rejected (error in the envelope).</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Data methods return envelope error 61002 (IP not allowed).</summary>
    public bool IpBlocked { get; set; }

    /// <summary>Data calls never answer (until cancelled).</summary>
    public bool Hang { get; set; }

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

    public static string AccountNumber(int seed, int index) => $"20208000{seed % 1000:D3}{index + 1:D6}001";

    /// <summary>APILogin response. The password is only checked for presence.</summary>
    public string LoginJson(Guid connectionId, string login, string password)
    {
        var s = For(connectionId);
        Interlocked.Increment(ref s.LoginCount);
        if (s.AuthFailure || string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            return ErrorEnvelope(10001, "Invalid login or password");
        var sid = "sid-" + Guid.NewGuid().ToString("N");
        s.Sessions[sid] = true;
        return Envelope(new JsonObject { ["sid"] = sid });
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
                ["account"] = AccountNumber(s.Seed, i),
                ["name"] = $"OOO \"FAKE CLIENT {s.Seed}\"",
                ["val"] = "000",
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
        var random = new Random(unchecked(s.Seed * 7919 + day.DayNumber * 31 + accountIndex));

        var content = new JsonArray();
        long debit = 0, credit = 0;
        var count = s.EmptyDays.Contains(day) ? 0 : random.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            var dir = random.Next(1, 3);
            var amount = (long)random.Next(1_000, 50_000_000) * 100; // whole sums, expressed in tiyin
            if (dir == 1) credit += amount; else debit += amount;
            var op = Operation(s, account, day, accountIndex, i, dir, amount, random, err: 0);
            content.Add(op);
            if (s.DuplicateDays.Contains(day)) content.Add(op.DeepClone());
        }

        if (s.ErroneousDays.Contains(day))
        {
            // Rejected payment: present in the statement, absent from the totals.
            content.Add(Operation(s, account, day, accountIndex, 900, 2, 777_00, random, ErroneousOperationErrorCode));
        }

        var saldoIn = 1_000_000_000_00L + (day.DayNumber % 100) * 1_000_00L;
        var result = new JsonObject
        {
            ["content"] = content,
            ["total_debit"] = debit,
            ["total_credit"] = credit,
            ["saldo_in"] = saldoIn,
            ["saldo_out"] = saldoIn + credit - debit,
            ["fin"] = s.OpenDays.Contains(day) ? 0 : 1,
            ["oper_day"] = day.ToString("yyyy-MM-dd"),
        };
        return Envelope(result);
    }

    private static JsonObject Operation(
        FakeBankScenario s, string account, DateOnly day, int accountIndex, int i, int dir, long amount, Random random, int err)
    {
        var partnerInn = (300_000_000 + random.Next(0, 99_999_999)).ToString();
        var partnerAccount = $"20208000{random.Next(0, 999_999_999):D9}001";
        var partnerName = $"OOO \"PARTNER {partnerInn[^3..]}\"";
        var ourInn = (200_000_000 + s.Seed).ToString();
        var ourName = $"OOO \"FAKE CLIENT {s.Seed}\"";
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
            ["purpose"] = dir == 1 ? $"Payment for goods under contract #{i + 10}" : $"Payment for services, invoice #{i + 20}",
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
        if (s.Hang) await Task.Delay(Timeout.Infinite, ct);
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
