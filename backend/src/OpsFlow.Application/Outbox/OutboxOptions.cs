using OpsFlow.Domain.Notifications;

namespace OpsFlow.Application.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 50;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 8;
    public int BaseDelaySeconds { get; set; } = 5;
    public int MaxDelaySeconds { get; set; } = 900;
    public int CompletedRetentionDays { get; set; } = 14;

    /// <summary>Notification types that are also e-mailed (in addition to the in-app notification). Dead letters are kept until an operator handles them.</summary>
    public NotificationType[] EmailNotificationTypes { get; set; } =
        [
            NotificationType.TicketAssigned,
            NotificationType.SlaWarning,
            NotificationType.SlaBreached,
        ];
}

/// <summary>A failure a retry cannot fix (unknown event type, invalid configuration): the event goes straight to the dead letters.</summary>
public sealed class OutboxPermanentException(string message, Exception? inner = null)
    : Exception(message, inner);
