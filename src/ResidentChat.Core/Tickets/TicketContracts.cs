namespace ResidentChat.Core.Tickets;

// Explicit numeric values are persisted in PostgreSQL; do not renumber.
public enum TicketCategory : short
{
    Heating = 1,
    Water = 2,
    Electricity = 3,
    Elevator = 4,
    CommonArea = 5,
    Other = 6
}

public enum TicketPriority : short
{
    Routine = 1,
    Urgent = 2
}

public enum TicketStatus : short
{
    Open = 1,
    Escalated = 2,
    Resolved = 3
}

public sealed record TicketSnapshot(
    Guid TicketId,
    Guid TenantId,
    TicketCategory Category,
    TicketPriority Priority,
    TicketStatus Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum TicketMutationOutcome { Updated, NotFound, Conflict }
public sealed record TicketMutationResult(TicketMutationOutcome Outcome, TicketSnapshot? Ticket = null);

public static class TicketRules
{
    public static bool IsCategory(TicketCategory value) =>
        value is TicketCategory.Heating or TicketCategory.Water or
        TicketCategory.Electricity or TicketCategory.Elevator or
        TicketCategory.CommonArea or TicketCategory.Other;

    public static bool IsPriority(TicketPriority value) =>
        value is TicketPriority.Routine or TicketPriority.Urgent;

    public static bool CanTransition(TicketStatus current, TicketStatus target, bool isSupervisor) =>
        current switch
        {
            TicketStatus.Open when target == TicketStatus.Escalated => true,
            TicketStatus.Open or TicketStatus.Escalated
                when target == TicketStatus.Resolved && isSupervisor => true,
            _ => false
        };
}
