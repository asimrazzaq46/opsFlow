using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Outbox;

/// <summary>Independent delivery destinations of one event; each is tracked durably so a retry never repeats a finished one.</summary>
[Flags]
public enum OutboxChannels
{
    None = 0,
    Realtime = 1,
    Email = 2,
    Cache = 4,
}
