namespace Synect.Mcp.Responses;

/// <summary>How the model should show an answer to the user (the Synect answer standard, docs/answer-standard.md).</summary>
public enum AnswerView
{
    /// <summary>One or two sentences (status, a choice to ask about, a confirmation).</summary>
    Text,

    /// <summary>The key figure first (bold, with period and currency), then one or two lines of explanation.</summary>
    Kpi,

    /// <summary>A compact table of the first rows with only the columns that answer the question.</summary>
    Table,

    /// <summary>Two periods or two sides next to each other with the difference.</summary>
    Comparison,

    /// <summary>A series over time or a ranking: a simple chart, or a short table when the client cannot draw one.</summary>
    Chart,

    /// <summary>Possible problems: the count and the amount first, then the rows by priority, each with its basis.</summary>
    Issues,

    /// <summary>Documents: number, date, counterparty, amount, status.</summary>
    Documents,

    /// <summary>One item in depth (why a payment was matched with a document).</summary>
    Details,
}

/// <summary>What the data of an answer is, so that the model never presents an estimate or a finding as a fact.</summary>
public enum AnswerBasis
{
    /// <summary>Figures as loaded from the source (bank, Didox, 1C).</summary>
    Fact,

    /// <summary>Computed from several sources by rules (debt estimate, payment delay); say "оценка".</summary>
    Estimate,

    /// <summary>A possible match of a payment with a document; the accountant decides.</summary>
    PossibleMatch,

    /// <summary>A possible discrepancy found by rules; never a confirmed error.</summary>
    PossibleDiscrepancy,
}

public enum ChartKind
{
    Line,
    Bar,
}

/// <param name="ShowRows">Rows to show in the first answer; the rest only when the user asks. Null: show the answer whole.</param>
public sealed record AnswerFormat(AnswerView View, AnswerBasis Basis, ChartKind? Chart = null, int? ShowRows = null);

/// <summary>
/// The presentation hint of every tool. Every registered tool must have one (enforced by a test); the hint goes into
/// the 'presentation' block of a successful answer and never changes the data.
/// </summary>
public static class AnswerFormats
{
    /// <summary>Rows in the first answer by view: problems are summarized harder than plain lists.</summary>
    public const int IssueRows = 5;
    public const int ListRows = 10;

    private static AnswerFormat Text(AnswerBasis b = AnswerBasis.Fact) => new(AnswerView.Text, b);
    private static AnswerFormat Kpi(AnswerBasis b = AnswerBasis.Fact) => new(AnswerView.Kpi, b);
    private static AnswerFormat Table(AnswerBasis b = AnswerBasis.Fact) => new(AnswerView.Table, b, ShowRows: ListRows);
    private static AnswerFormat Docs(AnswerBasis b = AnswerBasis.Fact) => new(AnswerView.Documents, b, ShowRows: ListRows);
    private static AnswerFormat Issues(AnswerBasis b = AnswerBasis.PossibleDiscrepancy) => new(AnswerView.Issues, b, ShowRows: IssueRows);
    private static AnswerFormat Chart(ChartKind kind) => new(AnswerView.Chart, AnswerBasis.Fact, kind);
    private static AnswerFormat Compare(AnswerBasis b = AnswerBasis.Fact) => new(AnswerView.Comparison, b, ShowRows: ListRows);

    public static readonly IReadOnlyDictionary<string, AnswerFormat> ByTool = new Dictionary<string, AnswerFormat>(StringComparer.Ordinal)
    {
        // Sources.
        ["get_data_freshness"] = Text(),
        ["request_data_refresh"] = Text(),

        // Money.
        ["get_account_balances"] = Kpi(),
        ["get_balance_history"] = Chart(ChartKind.Line),
        ["get_balance_stats"] = Kpi(),
        ["get_cash_flow"] = Chart(ChartKind.Bar),
        ["get_largest_transactions"] = Table(),
        ["get_transactions_for_day"] = Table(),
        ["find_idle_accounts"] = Table(),
        ["check_balance_integrity"] = Issues(),

        // Counterparties.
        ["find_counterparties"] = Text(),
        ["get_counterparty_payments"] = Kpi(),
        ["get_top_counterparties"] = Chart(ChartKind.Bar),
        ["get_counterparty_summary"] = Kpi(),
        ["compare_counterparty_periods"] = Compare(),
        ["get_counterparty_changes"] = Table(),
        ["get_payments_without_documents"] = Issues(),
        ["get_counterparty_concentration"] = Chart(ChartKind.Bar),

        // Didox documents.
        ["get_documents_awaiting_our_signature"] = Docs(),
        ["get_documents_awaiting_partner_signature"] = Docs(),
        ["get_document_counts"] = Kpi(),
        ["get_rejected_documents"] = Docs(),
        ["search_documents"] = Docs(),
        ["get_largest_documents"] = Docs(),
        ["get_vat_summary"] = Compare(),

        // Reconciliation of payments with documents.
        ["get_paid_documents"] = Docs(AnswerBasis.PossibleMatch),
        ["get_unpaid_documents"] = Docs(AnswerBasis.PossibleMatch),
        ["get_partially_paid_documents"] = Docs(AnswerBasis.PossibleMatch),
        ["get_signed_unpaid_acts"] = Docs(AnswerBasis.PossibleMatch),
        ["get_unmatched_payments"] = Issues(),
        ["get_multi_document_payments"] = Table(AnswerBasis.PossibleMatch),
        ["get_amount_differences"] = Issues(),
        ["get_payments_without_reference"] = Issues(),
        ["get_payment_delay_stats"] = Kpi(AnswerBasis.Estimate),
        ["get_account_mismatches"] = Issues(),
        ["get_possible_duplicate_payments"] = Issues(),
        ["get_counterparty_balance_estimate"] = Kpi(AnswerBasis.Estimate),
        ["get_match_details"] = new(AnswerView.Details, AnswerBasis.PossibleMatch),
        ["confirm_match"] = Text(),
        ["reject_match"] = Text(),

        // Attention.
        ["get_attention_items"] = Issues(),
        ["get_attention_summary"] = Kpi(AnswerBasis.PossibleDiscrepancy),
        ["update_attention_item"] = Text(),

        // Portfolio (all organizations of the key).
        ["list_organizations"] = Table(),
        ["get_portfolio_overview"] = Table(AnswerBasis.PossibleDiscrepancy),
        ["get_connections_overview"] = Table(),

        // 1C.
        ["get_1c_documents"] = Docs(),
        ["get_1c_payment_orders"] = Docs(),
        ["get_1c_settlement_balances"] = Table(),
        ["get_1c_bank_account_balances"] = Kpi(),
        ["compare_bank_with_1c"] = Issues(),
        ["compare_didox_with_1c"] = Issues(),
        ["compare_bank_balances_with_1c"] = Compare(AnswerBasis.PossibleDiscrepancy),
        ["compare_counterparty_with_1c"] = Issues(),
        ["get_1c_stock"] = Table(),
        ["get_1c_stock_summary"] = Kpi(),
        ["get_1c_prices"] = Table(),
    };

    /// <summary>
    /// The 'presentation' block: view, basis, chart, show_rows and more_on_request (true when the answer has more rows
    /// than show_rows or was truncated). Null for a tool without a hint.
    /// </summary>
    public static object? Block(string tool, int rows, bool truncated)
    {
        if (!ByTool.TryGetValue(tool, out var f)) return null;
        return new
        {
            f.View,
            f.Basis,
            f.Chart,
            f.ShowRows,
            MoreOnRequest = f.ShowRows is { } show && (rows > show || truncated),
        };
    }
}
