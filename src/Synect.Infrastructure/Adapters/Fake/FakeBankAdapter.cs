using Synect.Application.Adapters;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.KapitalBank;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// Bank adapter backed by <see cref="FakeBankDataSource"/>. Uses the real <see cref="KapitalBankMapper"/>
/// and <see cref="KapitalSessionManager"/> (login, sid cache, re-login on authentication errors), so
/// swapping in the real transport later does not change mapping or session handling.
/// </summary>
public sealed class FakeBankAdapter(FakeBankDataSource source, KapitalBankMapper mapper, KapitalSessionManager sessions) : IBankAdapter
{
    public const string Code = "fake_kapitalbank";
    public const string Provider = "KapitalBank";

    public string SourceCode => Code;
    public string ProviderName => Provider;
    public AdapterKind Kind => AdapterKind.Bank;

    public BankAdapterCapabilities Capabilities { get; } = new(
        SupportsDateRange: false,
        HistoryDepthDays: 365,
        HasFinalityFlag: true,
        HasStableOperationId: true);

    public async Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            await GetAccountsAsync(context, ct);
            return ConnectionTestResult.Ok();
        }
        catch (AdapterException ex) when (ex is not AdapterTransientException)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public Task<BankAccountList> GetAccountsAsync(AdapterContext context, CancellationToken ct) =>
        sessions.WithSessionAsync(Configured(context), Login(context), async (sid, token) =>
            mapper.MapAccounts(await source.AccountsJsonAsync(context.ConnectionId, sid, token)), ct);

    public Task<BankDayStatement> GetDayAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct) =>
        sessions.WithSessionAsync(Configured(context), Login(context), async (sid, token) =>
        {
            var json = await source.StatementJsonAsync(context.ConnectionId, sid, accountNumber, day, token);
            return new BankDayStatement(mapper.MapTransactions(json, accountNumber, day), mapper.MapBalance(json, accountNumber, day));
        }, ct);

    /// <summary>Applies the connection's stored demo settings before the first call of this process.</summary>
    private AdapterContext Configured(AdapterContext context)
    {
        source.Configure(context);
        return context;
    }

    private KapitalSessionManager.LoginTransport Login(AdapterContext context) =>
        (login, password, _) => Task.FromResult(source.LoginJson(context.ConnectionId, login, password));
}
