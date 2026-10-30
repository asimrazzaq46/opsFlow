using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>
/// One SLA cycle of a ticket. The first cycle starts at creation; every reopen starts a NEW cycle. Targets are SNAPSHOTTED from
/// the policy so later policy edits never rewrite history, and finished cycles are never modified again.
/// </summary>
public sealed class TicketSlaCycle : ITenantOwned
{
    private TicketSlaCycle()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public int CycleNumber { get; private set; }
    public SlaCycleReason Reason { get; private set; }
    public Guid SlaPolicyId { get; private set; }
    public int ResponseMinutes { get; private set; }
    public int ResolutionMinutes { get; private set; }
    public DateTime StartedAtUtc { get; private set; }

    /// <summary>Null for reopened cycles: a reopen only carries a resolution target.</summary>
    public DateTime? ResponseDueAtUtc { get; private set; }
    public DateTime ResolutionDueAtUtc { get; private set; }

    /// <summary>Set by the future customer-visible reply (Milestone 3); never fabricated here.</summary>
    public DateTime? FirstResponseAtUtc { get; private set; }

    /// <summary>Completion instant of the resolution target; null while the cycle is active.</summary>
    public DateTime? ResolvedAtUtc { get; private set; }

    public bool IsActive => ResolvedAtUtc is null;

    public static TicketSlaCycle Start(Ticket ticket, int cycleNumber, SlaCycleReason reason, SlaPolicy policy, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = ticket.TenantId,
        TicketId = ticket.Id,
        CycleNumber = cycleNumber,
        Reason = reason,
        SlaPolicyId = policy.Id,
        ResponseMinutes = policy.ResponseMinutes,
        ResolutionMinutes = policy.ResolutionMinutes,
        StartedAtUtc = nowUtc,
        ResponseDueAtUtc = reason == SlaCycleReason.Initial ? nowUtc.AddMinutes(policy.ResponseMinutes) : null,
        ResolutionDueAtUtc = nowUtc.AddMinutes(policy.ResolutionMinutes),
    };

    public void Resolve(DateTime nowUtc) => ResolvedAtUtc ??= nowUtc;

    /// <summary>A priority change re-applies the new policy to the ACTIVE cycle only, measured from the cycle start.</summary>
    public void ReapplyPolicy(SlaPolicy policy)
    {
        if (!IsActive)
        {
            return;
        }

        SlaPolicyId = policy.Id;
        ResponseMinutes = policy.ResponseMinutes;
        ResolutionMinutes = policy.ResolutionMinutes;
        ResolutionDueAtUtc = StartedAtUtc.AddMinutes(policy.ResolutionMinutes);
        if (Reason == SlaCycleReason.Initial && FirstResponseAtUtc is null)
        {
            ResponseDueAtUtc = StartedAtUtc.AddMinutes(policy.ResponseMinutes);
        }
    }
}

/// <summary>Pure SLA state calculation from persisted instants and the current time. A deadline is met when completed at-or-before it and overdue only strictly after it.</summary>
public static class SlaEvaluation
{
    public static SlaState Resolution(TicketSlaCycle cycle, DateTime nowUtc) =>
        cycle.ResolvedAtUtc is { } resolved
            ? (resolved <= cycle.ResolutionDueAtUtc ? SlaState.Met : SlaState.Missed)
            : (nowUtc > cycle.ResolutionDueAtUtc ? SlaState.Overdue : SlaState.Running);

    public static SlaState Response(TicketSlaCycle cycle, DateTime nowUtc) =>
        cycle.ResponseDueAtUtc is not { } due
            ? SlaState.NotApplicable
            : cycle.FirstResponseAtUtc is { } first
                ? (first <= due ? SlaState.Met : SlaState.Missed)
                : (nowUtc > due ? SlaState.Overdue : SlaState.Running);

    public static double? RemainingResolutionMinutes(TicketSlaCycle cycle, DateTime nowUtc) =>
        cycle.IsActive ? (cycle.ResolutionDueAtUtc - nowUtc).TotalMinutes : null;
}
