using ResidentChat.Core.Tickets;

namespace ResidentChat.Core.Demo;

/// <summary>
/// Local laboratory approval boundary: a proposal alone has no authority.
/// The caller must additionally be an authenticated operator of the same
/// tenant and supply a case-specific affirmative confirmation. This class
/// does not authenticate JWT or perform persistence; the existing protected
/// operator HTTP API must independently authorize the actual POST.
/// </summary>
public sealed record ApprovedDemoTicketCommand(
    Guid AuthorizedTenantId,
    TicketCategory Category,
    TicketPriority Priority);

public static class DemoApprovalPolicy
{
    public static bool TryApprove(
        Guid authenticatedOperatorTenantId,
        string? caseId,
        DemoDispatchResult? planned,
        string? explicitConfirmation,
        out ApprovedDemoTicketCommand? approved)
    {
        approved = null;
        if (authenticatedOperatorTenantId == Guid.Empty
            || caseId is null || caseId.Length is < 3 or > 32
            || !caseId.All(ch => ch is >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-')
            || !string.Equals(explicitConfirmation,
                "APPROVE " + caseId, StringComparison.Ordinal)
            || planned is not { Queue: DemoQueueKind.TicketProposal,
                Proposal: { RequiresHumanApproval: true } proposal }
            || proposal.AuthorizedTenantId != authenticatedOperatorTenantId
            || !TicketRules.IsCategory(proposal.Category)
            || !TicketRules.IsPriority(proposal.Priority))
            return false;

        // Never pass message text, model output or arbitrary caller fields
        // to the authenticated API. The operator API accepts exactly two enums.
        approved = new ApprovedDemoTicketCommand(
            authenticatedOperatorTenantId,
            proposal.Category,
            proposal.Priority);
        return true;
    }
}
