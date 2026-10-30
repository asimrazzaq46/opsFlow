using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Per-tenant ticket settings (one row per tenant). Only what the workflow needs: the ticket number prefix.</summary>
public sealed class TenantSettings : ITenantOwned
{
    public const string DefaultTicketPrefix = "TKT";

    private TenantSettings()
    {
        TicketNumberPrefix = DefaultTicketPrefix;
    }

    public Guid TenantId { get; private set; }
    public string TicketNumberPrefix { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static TenantSettings Create(Guid tenantId, DateTime nowUtc) => new()
    {
        TenantId = tenantId,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    public static string FormatNumber(string prefix, long number) => $"{prefix}-{number:D5}";
}

/// <summary>Per-tenant ticket number sequence. The next number is taken with an atomic in-database increment, never MAX()+1.</summary>
public sealed class TenantTicketCounter : ITenantOwned
{
    private TenantTicketCounter()
    {
    }

    public Guid TenantId { get; private set; }
    public long LastNumber { get; private set; }

    public static TenantTicketCounter Create(Guid tenantId) => new() { TenantId = tenantId };
}
