using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>One agent working on a ticket. Rows are never deleted: unassigning closes the row so history is preserved.</summary>
public sealed class TicketAssignment : ITenantOwned
{
    private TicketAssignment()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid MembershipId { get; private set; }
    public Guid AssignedByMembershipId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }
    public Guid? UnassignedByMembershipId { get; private set; }
    public DateTime? UnassignedAtUtc { get; private set; }

    public bool IsActive => UnassignedAtUtc is null;

    public static TicketAssignment Create(Guid tenantId, Guid ticketId, Guid membershipId, Guid assignedByMembershipId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TicketId = ticketId,
        MembershipId = membershipId,
        AssignedByMembershipId = assignedByMembershipId,
        AssignedAtUtc = nowUtc,
    };

    public void Unassign(Guid byMembershipId, DateTime nowUtc)
    {
        UnassignedByMembershipId = byMembershipId;
        UnassignedAtUtc = nowUtc;
    }
}
