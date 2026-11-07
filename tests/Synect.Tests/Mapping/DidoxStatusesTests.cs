using Synect.Infrastructure.Adapters.Didox;

namespace Synect.Tests.Mapping;

public class DidoxStatusesTests
{
    [Theory]
    [InlineData("0", DidoxDocumentStatus.Draft, "draft")]
    [InlineData("1", DidoxDocumentStatus.AwaitingPartnerSignature, "awaiting partner signature")]
    [InlineData("2", DidoxDocumentStatus.AwaitingOurSignature, "awaiting our signature")]
    [InlineData("3", DidoxDocumentStatus.Signed, "signed")]
    [InlineData("4", DidoxDocumentStatus.Rejected, "rejected")]
    [InlineData("6", DidoxDocumentStatus.AwaitingAgentSignature, "awaiting agent signature")]
    [InlineData("8", DidoxDocumentStatus.SignedByAgent, "signed by agent")]
    [InlineData("40", DidoxDocumentStatus.Invalid, "invalid")]
    [InlineData("60", DidoxDocumentStatus.AwaitingAgentSignatureSpecialScenario, "awaiting agent signature (special scenario)")]
    [InlineData("140", DidoxDocumentStatus.AcceptedByResponsiblePerson, "accepted by responsible person")]
    [InlineData("160", DidoxDocumentStatus.Delivered, "delivered")]
    [InlineData("190", DidoxDocumentStatus.Returned, "returned")]
    [InlineData("240", DidoxDocumentStatus.OtherAcceptanceStage, "other acceptance stage")]
    public void Known_codes_are_described(string code, DidoxDocumentStatus status, string description)
    {
        Assert.Equal(status, DidoxStatuses.FromCode(code));
        Assert.Equal(description, DidoxStatuses.Describe(code));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("999")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_codes_do_not_fail(string? code)
    {
        Assert.Null(DidoxStatuses.FromCode(code));
        Assert.Null(DidoxStatuses.Describe(code));
    }
}
