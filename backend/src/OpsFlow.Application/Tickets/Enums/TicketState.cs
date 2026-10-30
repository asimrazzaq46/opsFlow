using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public enum TicketState
{
    Open,
    Finished,
    Overdue,

    /// <summary>Not finished and without any ELIGIBLE assignee (none, or only suspended/deactivated/re-roled agents). Staff only.</summary>
    Unstaffed,
}
