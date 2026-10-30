using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Outbox;

public enum OutboxStatus
{
    Pending,
    Claimed,
    RetryScheduled,
    Completed,
    DeadLetter,
}
