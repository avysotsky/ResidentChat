using ResidentChat.Core.Tickets;

namespace ResidentChat.Core.Demo;

/// <summary>
/// Laboratory gate. A classifier suggestion alone never authorizes ticket
/// creation. The real operator API independently authenticates a signed JWT
/// and derives the tenant from its claims before any database write.
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
            || string.IsNullOrWhiteSpace(caseId)
            || caseId.Length is < 3 or > 32
            || !caseId.All(ch => (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9') || ch == '-')
            || !string.Equals(explicitConfirmation,
                "APPROVE " + caseId, StringComparison.Ordinal))
            return false;

        if (planned?.Queue != DemoQueueKind.TicketProposal
            || planned.Proposal is not { RequiresHumanApproval: true } proposal)
            return false;

        if (proposal.AuthorizedTenantId != authenticatedOperatorTenantId
            || !TicketRules.IsCategory(proposal.Category)
            || !TicketRules.IsPriority(proposal.Priority))
            return false;

        // Only metadata enums cross the approval boundary, no raw messages,
        // model-generated SQL or additional authorization claims.
        approved = new ApprovedDemoTicketCommand(
            authenticatedOperatorTenantId,
            proposal.Category,
            proposal.Priority);
        return true;
    }
}
