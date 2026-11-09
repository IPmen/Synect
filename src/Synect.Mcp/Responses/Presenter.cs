using Microsoft.Extensions.Options;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Application.Analytics.Counterparties;

namespace Synect.Mcp.Responses;

/// <summary>
/// Turns analytics results into answer rows: money as {amount_minor, amount, currency}, account numbers masked,
/// third-party text wrapped and cleaned.
/// </summary>
public sealed class Presenter(IOptions<McpOptions> options)
{
    private McpOptions O => options.Value;

    public MoneyValue Money(long minor, string currency) => MoneyValue.Of(minor, currency);
    public string? Account(string? number) => TextSafety.MaskAccount(number, O.MaskAccountNumbers);
    public UntrustedValue? Untrusted(string? text) => TextSafety.Untrusted(text, O.UntrustedTextMaxLength);

    /// <summary>Identifiers from the source (document numbers etc.): cleaned and cut to 100 characters, not wrapped.</summary>
    public string? Code(string? text) => text is null ? null : TextSafety.Clean(text, 100, out _);

    public object Totals(IEnumerable<MoneyAmount> totals) => totals.Select(t => Money(t.Amount, t.Currency)).ToList();

    public object AccountBalance(AccountBalance a) => new
    {
        a.AccountId,
        AccountNumber = Account(a.AccountNumber),
        AccountName = Untrusted(a.AccountName),
        Balance = Money(a.Balance, a.Currency),
        BalanceDate = a.Date,
        a.IsDayClosed,
    };

    public object Operation(BankOperation o) => new
    {
        o.Id,
        o.AccountId,
        AccountNumber = Account(o.AccountNumber),
        OperationId = Code(o.OperationId),
        o.PostingDate,
        o.ValueDate,
        o.Direction,
        Amount = Money(o.Amount, o.Currency),
        o.CounterpartyInn,
        CounterpartyName = Untrusted(o.CounterpartyName),
        CounterpartyAccount = Account(o.CounterpartyAccount),
        Purpose = Untrusted(o.Purpose),
        DocumentNumber = Code(o.DocumentNumber),
        o.IsFinal,
        BankState = Code(o.SourceState),
        o.IsDayClosed,
    };

    public object DirectionTotal(DirectionTotal t) => new
    {
        Incoming = Money(t.Incoming, t.Currency),
        Outgoing = Money(t.Outgoing, t.Currency),
        t.IncomingCount,
        t.OutgoingCount,
    };

    public object Document(EdoDocumentItem d) => new
    {
        d.Id,
        ExternalId = Code(d.ExternalId),
        DocType = Code(d.DocType),
        d.Status,
        d.StatusName,
        StatusComment = Untrusted(d.StatusComment),
        d.Owner,
        Number = Code(d.Number),
        d.DocumentDate,
        d.CounterpartyInn,
        CounterpartyName = Untrusted(d.CounterpartyName),
        ContractNumber = Code(d.ContractNumber),
        d.ContractDate,
        Total = Money(d.TotalSum, d.Currency),
        Vat = Money(d.TotalVatSum, d.Currency),
        d.SourceCreatedAt,
        d.SourceUpdatedAt,
        d.AgeDays,
    };

    public object Name(string? name) => Untrusted(name)!;
}
