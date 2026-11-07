using Synect.Application.Adapters;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.KapitalBank;

namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// Bank adapter backed by <see cref="FakeBankDataSource"/>. Uses the real <see cref="KapitalBankMapper"/>
/// (including envelope error handling), so swapping in the real transport later does not change the mapping.
/// </summary>
public sealed class FakeBankAdapter(FakeBankDataSource source, KapitalBankMapper mapper) : IBankAdapter
{
    public const string Code = "fake_kapitalbank";

    public string SourceCode => Code;
    public AdapterKind Kind => AdapterKind.Bank;

    public BankAdapterCapabilities Capabilities { get; } = new(
        SupportsDateRange: false,
        HistoryDepthDays: 365,
        HasFinalityFlag: true,
        HasStableOperationId: true);

    public Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            mapper.MapAccounts(source.AccountsJson(context.ConnectionId));
            return Task.FromResult(ConnectionTestResult.Ok());
        }
        catch (AdapterException ex)
        {
            return Task.FromResult(ConnectionTestResult.Failed(ex.Message));
        }
    }

    public Task<IReadOnlyList<BankAccountInfo>> GetAccountsAsync(AdapterContext context, CancellationToken ct) =>
        Task.FromResult(mapper.MapAccounts(source.AccountsJson(context.ConnectionId)));

    public Task<BankDayTransactions> GetTransactionsAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct) =>
        Task.FromResult(mapper.MapTransactions(source.StatementJson(context.ConnectionId, accountNumber, day), accountNumber, day));

    public Task<BankBalanceInfo?> GetBalancesAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct) =>
        Task.FromResult(mapper.MapBalance(source.StatementJson(context.ConnectionId, accountNumber, day), accountNumber, day));
}
