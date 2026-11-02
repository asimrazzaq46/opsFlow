using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

/// <summary>Managers must name an eligible agent assigned to the ticket; Agents may omit it (they are always the assignee).</summary>
public sealed record CreateTicketTaskRequest(
    [Required, StringLength(TicketTask.TitleMaxLength, MinimumLength = 1)] string Title,
    [StringLength(TicketTask.DescriptionMaxLength)] string? Description,
    Guid? AssigneeMembershipId
);

/// <summary>Replaces title AND description: send the current description to keep it, null or empty to clear it.</summary>
public sealed record UpdateTicketTaskRequest(
    [Required, StringLength(TicketTask.TitleMaxLength, MinimumLength = 1)] string Title,
    [StringLength(TicketTask.DescriptionMaxLength)] string? Description,
    [Required] int? Version
);

public sealed record ChangeTicketTaskStatusRequest(
    [Required, EnumDataType(typeof(TicketTaskStatus))] TicketTaskStatus? Status,
    [Required] int? Version
);

public sealed record ReassignTicketTaskRequest(
    [Required] Guid? AssigneeMembershipId,
    [Required] int? Version
);

public sealed record TicketTaskOrderItem([Required] Guid? TaskId, [Required] int? Version);

/// <summary>The COMPLETE list of the ticket's tasks in the desired order, each with the version the client last saw.</summary>
public sealed record ReorderTicketTasksRequest(
    [Required, MinLength(1), MaxLength(500)] List<TicketTaskOrderItem> Tasks
);
