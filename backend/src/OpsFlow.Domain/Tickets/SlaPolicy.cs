using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Per-tenant, per-priority SLA targets, measured in elapsed (calendar) minutes. At most one ACTIVE policy per priority.</summary>
public sealed class SlaPolicy : ITenantOwned
{
    public const int MaxMinutes = 525_600; // one year

    private SlaPolicy() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public TicketPriority Priority { get; private set; }
    public int ResponseMinutes { get; private set; }
    public int ResolutionMinutes { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static SlaPolicy Create(
        Guid tenantId,
        TicketPriority priority,
        int responseMinutes,
        int resolutionMinutes,
        DateTime nowUtc
    )
    {
        Validate(responseMinutes, resolutionMinutes);
        return new SlaPolicy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Priority = priority,
            ResponseMinutes = responseMinutes,
            ResolutionMinutes = resolutionMinutes,
            IsActive = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    public void UpdateTargets(int responseMinutes, int resolutionMinutes, DateTime nowUtc)
    {
        Validate(responseMinutes, resolutionMinutes);
        ResponseMinutes = responseMinutes;
        ResolutionMinutes = resolutionMinutes;
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }

    private static void Validate(int responseMinutes, int resolutionMinutes)
    {
        if (
            responseMinutes is < 1 or > MaxMinutes
            || resolutionMinutes is < 1 or > MaxMinutes
            || resolutionMinutes < responseMinutes
        )
        {
            throw new ArgumentException(
                "SLA targets must be 1..525600 minutes and resolution must not be shorter than response."
            );
        }
    }
}
