using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>
/// Internal child work item of a ticket with exactly one responsible agent. It never changes the parent ticket's status, SLA or
/// assignments. Rows are never deleted, so history survives completion, reassignment and agent deactivation.
/// </summary>
public sealed class TicketTask : ITenantOwned
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 4000;

    private TicketTask()
    {
        Title = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public Guid AssigneeMembershipId { get; private set; }
    public TicketTaskStatus Status { get; private set; }
    public int SortOrder { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public Guid CreatedByMembershipId { get; private set; }
    public Guid? UpdatedByMembershipId { get; private set; }

    /// <summary>Optimistic concurrency token: every mutation increments it.</summary>
    public int Version { get; private set; }

    public static TicketTask Create(
        Guid tenantId,
        Guid ticketId,
        string title,
        string? description,
        Guid assigneeMembershipId,
        int sortOrder,
        Guid createdByMembershipId,
        DateTime nowUtc
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortOrder, 1);
        return new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketId = ticketId,
            Title = NormalizeTitle(title),
            Description = NormalizeDescription(description),
            AssigneeMembershipId = assigneeMembershipId,
            Status = TicketTaskStatus.Todo,
            SortOrder = sortOrder,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CreatedByMembershipId = createdByMembershipId,
            Version = 1,
        };
    }

    public void UpdateDetails(string title, string? description, Guid byMembershipId, DateTime nowUtc)
    {
        Title = NormalizeTitle(title);
        Description = NormalizeDescription(description);
        Touch(byMembershipId, nowUtc);
    }

    /// <summary>Done records the completion time; leaving Done clears it (the audit trail keeps every transition).</summary>
    public void ChangeStatus(TicketTaskStatus status, Guid byMembershipId, DateTime nowUtc)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        CompletedAtUtc = status == TicketTaskStatus.Done ? CompletedAtUtc ?? nowUtc : null;
        Status = status;
        Touch(byMembershipId, nowUtc);
    }

    public void Reassign(Guid assigneeMembershipId, Guid byMembershipId, DateTime nowUtc)
    {
        AssigneeMembershipId = assigneeMembershipId;
        Touch(byMembershipId, nowUtc);
    }

    public void MoveTo(int sortOrder, Guid byMembershipId, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortOrder, 1);
        SortOrder = sortOrder;
        Touch(byMembershipId, nowUtc);
    }

    public static string NormalizeTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= TitleMaxLength
            ? trimmed
            : throw new ArgumentException(
                $"Title must be 1-{TitleMaxLength} characters.",
                nameof(title)
            );
    }

    public static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= DescriptionMaxLength
            ? trimmed
            : throw new ArgumentException(
                $"Description must be at most {DescriptionMaxLength} characters.",
                nameof(description)
            );
    }

    private void Touch(Guid byMembershipId, DateTime nowUtc)
    {
        UpdatedByMembershipId = byMembershipId;
        UpdatedAtUtc = nowUtc;
        Version++;
    }
}
