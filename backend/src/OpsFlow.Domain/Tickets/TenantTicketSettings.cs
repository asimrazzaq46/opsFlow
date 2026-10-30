using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Per-tenant settings (one row per tenant): the ticket number prefix and the authoritative reporting timezone.</summary>
public sealed class TenantSettings : ITenantOwned
{
    public const string DefaultTicketPrefix = "TKT";
    public const string DefaultReportingTimeZone = "UTC";
    public const int MaxReportingTimeZoneLength = 64;

    private TenantSettings()
    {
        TicketNumberPrefix = DefaultTicketPrefix;
        ReportingTimeZone = DefaultReportingTimeZone;
    }

    public Guid TenantId { get; private set; }
    public string TicketNumberPrefix { get; private set; }

    // UPDATED: Milestone 5 - tenant ka authoritative reporting timezone (IANA id); validation Application layer mein, yahan sirf store
    public string ReportingTimeZone { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static TenantSettings Create(Guid tenantId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    public void SetReportingTimeZone(string ianaId, DateTime nowUtc)
    {
        ReportingTimeZone = ianaId;
        UpdatedAtUtc = nowUtc;
    }

    public static string FormatNumber(string prefix, long number) => $"{prefix}-{number:D5}";
}

/// <summary>Per-tenant ticket number sequence. The next number is taken with an atomic in-database increment, never MAX()+1.</summary>
public sealed class TenantTicketCounter : ITenantOwned
{
    private TenantTicketCounter() { }

    public Guid TenantId { get; private set; }
    public long LastNumber { get; private set; }

    public static TenantTicketCounter Create(Guid tenantId) => new() { TenantId = tenantId };
}
