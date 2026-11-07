namespace Synect.Infrastructure.Adapters.Didox;

/// <summary>
/// Known Didox document status codes. The raw code is still stored as received (EdoDocument.Status);
/// this is a lookup for display and filtering. Unknown codes are allowed.
/// </summary>
public enum DidoxDocumentStatus
{
    Draft = 0,
    AwaitingPartnerSignature = 1,
    AwaitingOurSignature = 2,
    Signed = 3,
    Rejected = 4,
    AwaitingAgentSignature = 6,
    SignedByAgent = 8,
    Invalid = 40,
    AwaitingAgentSignatureSpecialScenario = 60,

    // Waybill (TTN) workflow stages.
    AcceptedByResponsiblePerson = 140,
    Delivered = 160,
    Returned = 190,
    OtherAcceptanceStage = 240,
}

public static class DidoxStatuses
{
    private static readonly IReadOnlyDictionary<DidoxDocumentStatus, string> Descriptions = new Dictionary<DidoxDocumentStatus, string>
    {
        [DidoxDocumentStatus.Draft] = "draft",
        [DidoxDocumentStatus.AwaitingPartnerSignature] = "awaiting partner signature",
        [DidoxDocumentStatus.AwaitingOurSignature] = "awaiting our signature",
        [DidoxDocumentStatus.Signed] = "signed",
        [DidoxDocumentStatus.Rejected] = "rejected",
        [DidoxDocumentStatus.AwaitingAgentSignature] = "awaiting agent signature",
        [DidoxDocumentStatus.SignedByAgent] = "signed by agent",
        [DidoxDocumentStatus.Invalid] = "invalid",
        [DidoxDocumentStatus.AwaitingAgentSignatureSpecialScenario] = "awaiting agent signature (special scenario)",
        [DidoxDocumentStatus.AcceptedByResponsiblePerson] = "accepted by responsible person",
        [DidoxDocumentStatus.Delivered] = "delivered",
        [DidoxDocumentStatus.Returned] = "returned",
        [DidoxDocumentStatus.OtherAcceptanceStage] = "other acceptance stage",
    };

    /// <summary>Known status for a raw code, or null for an unknown or malformed code. Never throws.</summary>
    public static DidoxDocumentStatus? FromCode(string? code) =>
        int.TryParse(code, out var value) && Enum.IsDefined(typeof(DidoxDocumentStatus), value)
            ? (DidoxDocumentStatus)value
            : null;

    /// <summary>Human-readable description, or null for an unknown code.</summary>
    public static string? Describe(string? code) =>
        FromCode(code) is { } status ? Descriptions[status] : null;
}
