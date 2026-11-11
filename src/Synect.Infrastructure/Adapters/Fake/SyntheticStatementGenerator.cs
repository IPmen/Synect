using System.Globalization;
using System.Text;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// Builds large synthetic bank statements (fictitious data only) for volume tests and benchmarks: one MT940 message per
/// day with consistent opening and closing balances, counterparties with INN, account and MFO in :86:.
/// </summary>
public static class SyntheticStatementGenerator
{
    /// <param name="Account">20-digit account number of the statement (fictitious).</param>
    /// <param name="OpeningBalance">Minor units.</param>
    public sealed record Options(
        string Account,
        DateOnly From,
        int Days,
        int Operations,
        int Seed = 1,
        int Counterparties = 300,
        long OpeningBalance = 500_000_000_000,
        string Currency = "UZS",
        string InnPrefix = "39");

    public static string Mt940(Options o)
    {
        var random = new Random(o.Seed);
        var sb = new StringBuilder(o.Operations * 200);
        var balance = o.OpeningBalance;
        var perDay = o.Operations / o.Days;
        var extra = o.Operations % o.Days;
        for (var d = 0; d < o.Days; d++)
        {
            var day = o.From.AddDays(d);
            var count = perDay + (d < extra ? 1 : 0);
            sb.Append(":20:SYN").Append(day.ToString("yyMMdd", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(":25:").Append(o.Account).Append("\r\n");
            sb.Append(":28C:").Append(d + 1).Append("/1\r\n");
            sb.Append(":60F:").Append(Balance(balance, day, o.Currency)).Append("\r\n");
            for (var i = 0; i < count; i++)
            {
                var cp = random.Next(o.Counterparties);
                var inn = o.InnPrefix + cp.ToString("D7", CultureInfo.InvariantCulture);
                var incoming = random.NextDouble() < 0.45;
                var amount = (long)random.Next(1_000, 5_000_000) * 100 + random.Next(100);
                if (!incoming && amount > balance) incoming = true;
                balance += incoming ? amount : -amount;
                sb.Append(":61:").Append(day.ToString("yyMMdd", CultureInfo.InvariantCulture)).Append(day.ToString("MMdd", CultureInfo.InvariantCulture))
                    .Append(incoming ? 'C' : 'D').Append(Amount(amount)).Append("NTRFNONREF\r\n");
                var doc = random.Next(1, 99_999);
                sb.Append(":86:").Append(incoming ? "Оплата от " : "Оплата ").Append("ООО Синтетика ").Append(cp).Append(" ИНН ").Append(inn)
                    .Append(" СЧ 20208000").Append((900_000_000_000L + cp).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                    .Append("МФО 00").Append(400 + cp % 100).Append(" по счету-фактуре № ").Append(doc)
                    .Append(" от ").Append(day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append(" без НДС\r\n");
            }
            sb.Append(":62F:").Append(Balance(balance, day, o.Currency)).Append("\r\n");
            sb.Append("-\r\n");
        }
        return sb.ToString();
    }

    private static string Balance(long amount, DateOnly day, string currency) =>
        (amount < 0 ? "D" : "C") + day.ToString("yyMMdd", CultureInfo.InvariantCulture) + currency + Amount(Math.Abs(amount));

    private static string Amount(long minor) =>
        (minor / 100).ToString(CultureInfo.InvariantCulture) + "," + (minor % 100).ToString("D2", CultureInfo.InvariantCulture);
}
