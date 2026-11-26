using System.Text.Json;
using Synect.Mcp.Hosting;
using Synect.Mcp.Responses;

namespace Synect.Tests.Mcp;

/// <summary>The answer standard: every tool has a presentation hint, and the hint is consistent with the data it labels.</summary>
public class AnswerFormatTests
{
    [Fact]
    public void Every_tool_has_a_presentation_hint_and_no_hint_is_left_over()
    {
        Assert.Equal(McpToolTests.AllTools.Order(), AnswerFormats.ByTool.Keys.Order());
    }

    [Fact]
    public void Lists_show_a_few_rows_first_and_whole_answers_have_no_row_limit()
    {
        foreach (var (tool, f) in AnswerFormats.ByTool)
        {
            var isList = f.View is AnswerView.Table or AnswerView.Documents or AnswerView.Issues or AnswerView.Comparison;
            Assert.True(isList == f.ShowRows is not null, tool);
            Assert.True((f.View == AnswerView.Chart) == f.Chart is not null, tool);
            if (f.View == AnswerView.Issues) Assert.Equal(AnswerFormats.IssueRows, f.ShowRows);
        }
    }

    [Theory]
    // Findings of rules are never presented as facts.
    [InlineData("get_unmatched_payments", "possible_discrepancy")]
    [InlineData("get_possible_duplicate_payments", "possible_discrepancy")]
    [InlineData("compare_bank_with_1c", "possible_discrepancy")]
    [InlineData("get_attention_items", "possible_discrepancy")]
    [InlineData("get_paid_documents", "possible_match")]
    [InlineData("get_match_details", "possible_match")]
    // Debt by bank and Didox is an estimate; debt by 1C is the accounting figure.
    [InlineData("get_counterparty_balance_estimate", "estimate")]
    [InlineData("get_1c_settlement_balances", "fact")]
    [InlineData("get_account_balances", "fact")]
    public void Basis_tells_facts_from_estimates_and_findings(string tool, string basis)
    {
        var block = Serialize(AnswerFormats.Block(tool, 1, false));
        Assert.Equal(basis, block.GetProperty("basis").GetString());
    }

    [Fact]
    public void More_on_request_only_when_the_answer_has_more_rows_than_shown()
    {
        Assert.False(Serialize(AnswerFormats.Block("get_unmatched_payments", 5, false)).GetProperty("more_on_request").GetBoolean());
        Assert.True(Serialize(AnswerFormats.Block("get_unmatched_payments", 6, false)).GetProperty("more_on_request").GetBoolean());
        Assert.True(Serialize(AnswerFormats.Block("get_largest_transactions", 3, true)).GetProperty("more_on_request").GetBoolean());
        // A figure is shown whole.
        var kpi = Serialize(AnswerFormats.Block("get_account_balances", 40, false));
        Assert.Equal("kpi", kpi.GetProperty("view").GetString());
        Assert.False(kpi.GetProperty("more_on_request").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kpi.GetProperty("show_rows").ValueKind);
        Assert.Null(AnswerFormats.Block("no_such_tool", 1, false));
    }

    [Fact]
    public void Chart_answers_name_the_chart_kind()
    {
        Assert.Equal("line", Serialize(AnswerFormats.Block("get_balance_history", 30, false)).GetProperty("chart").GetString());
        Assert.Equal("bar", Serialize(AnswerFormats.Block("get_cash_flow", 5, false)).GetProperty("chart").GetString());
    }

    [Fact]
    public void Every_answer_carries_its_instruction_because_clients_may_drop_the_server_instructions()
    {
        var cash = Serialize(AnswerFormats.Block("get_cash_flow", 5, false)).GetProperty("how").GetString()!;
        Assert.Contains("came in, what went out and the net", cash);
        Assert.Contains("never with a line about looking up", cash);
        var issues = Serialize(AnswerFormats.Block("get_unmatched_payments", 50, true)).GetProperty("how").GetString()!;
        Assert.Contains("'summary'", issues);
        Assert.Contains("Offer the rest in one line", issues);
        Assert.Contains("possible discrepancy", issues);
        Assert.DoesNotContain("Offer the rest", Serialize(AnswerFormats.Block("get_unmatched_payments", 3, false)).GetProperty("how").GetString());
    }

    [Fact]
    public void Server_instructions_carry_the_answer_standard()
    {
        var s = McpServiceCollection.AnswerStandard;
        foreach (var part in new[]
                 {
                     "never to the job title", "no preamble", "100-150 words", "'presentation' block", "more_on_request",
                     "possible_discrepancy", "period and the currency", "Можно проверить:", "never tell the user to pay",
                 })
            Assert.Contains(part, s);
    }

    private static JsonElement Serialize(object? block) => JsonSerializer.SerializeToElement(block, ToolJson.Options);
}
