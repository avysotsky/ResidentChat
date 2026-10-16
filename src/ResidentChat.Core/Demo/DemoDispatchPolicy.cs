using ResidentChat.Core.LocalAi;
using ResidentChat.Core.Tickets;

namespace ResidentChat.Core.Demo;

/// <summary>
/// Deliberately NON-PERSISTENT proposal routing. An actual ticket can only be
/// created by the existing authenticated operator API, never by this class
/// or by a model's recommendation.
/// </summary>
public enum DemoQueueKind
{
    TicketProposal,
    OperatorReview,
    ApprovedFaqRequired,
    ModerationReview
}

public sealed record DemoTicketProposal(
    Guid AuthorizedTenantId,
    TicketCategory Category,
    TicketPriority Priority,
    TicketStatus SuggestedInitialStatus,
    bool RequiresHumanApproval);

public sealed record DemoDispatchResult(
    DemoQueueKind Queue,
    DemoTicketProposal? Proposal = null);

public static class DemoDispatchPolicy
{
    /// <summary>
    /// authorizedTenantId must originate from a trusted synthetic fixture
    /// (or verified identity in a future application), NEVER model text.
    /// This method has no side effects, storage, network, or Telegram access.
    /// </summary>
    public static DemoDispatchResult Plan(Guid authorizedTenantId, ResidentDecision? decision)
    {
        if (authorizedTenantId == Guid.Empty || decision is not { Valid: true })
            return new(DemoQueueKind.OperatorReview);

        // Urgent human safety is higher priority than anything a model recommends.
        // Emergency classification cannot create or close tickets automatically.
        if (decision.Intent is ResidentIntent.Emergency or ResidentIntent.Conflict
            || decision.Urgency == ResidentUrgency.Emergency)
            return new(DemoQueueKind.OperatorReview);

        // A high-urgency, human-required decision cannot be hidden in an FAQ
        // or moderation queue by a mistaken (or adversarial) model intent.
        // In particular this preserves the C# shared-area water-leak floor
        // even if the model predicts FAQ or Spam instead of Maintenance.
        if (decision.RequiresHuman && decision.Urgency == ResidentUrgency.Urgent
            && decision.Intent is not ResidentIntent.Maintenance)
            return new(DemoQueueKind.OperatorReview);

        if (decision.Intent == ResidentIntent.Faq)
            return new(DemoQueueKind.ApprovedFaqRequired);

        if (decision.Intent == ResidentIntent.Spam)
            return new(DemoQueueKind.ModerationReview);

        if (decision.Intent != ResidentIntent.Maintenance
            || !TryMapCategory(decision.Category, out var category))
            return new(DemoQueueKind.OperatorReview);

        var priority = decision.Urgency == ResidentUrgency.Urgent
            ? TicketPriority.Urgent : TicketPriority.Routine;
        var status = priority == TicketPriority.Urgent || decision.RequiresHuman
            ? TicketStatus.Escalated : TicketStatus.Open;

        // This is a suggestion, not a committed TicketSnapshot.
        // Human authorization must precede ALL actual ticket writes.
        return new(DemoQueueKind.TicketProposal,
            new DemoTicketProposal(authorizedTenantId, category, priority,
                status, RequiresHumanApproval: true));
    }

    private static bool TryMapCategory(string? source, out TicketCategory category)
    {
        category = source switch
        {
            "heating" => TicketCategory.Heating,
            "water" => TicketCategory.Water,
            "electricity" => TicketCategory.Electricity,
            "elevator" => TicketCategory.Elevator,
            "common_area" => TicketCategory.CommonArea,
            "other" => TicketCategory.Other,
            _ => default
        };
        return TicketRules.IsCategory(category);
    }
}
