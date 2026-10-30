using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

public sealed class TicketCategory : ITenantOwned
{
    private TicketCategory()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string NormalizedName { get; private set; }

    /// <summary>Inactive categories stay valid for historical tickets but cannot be chosen for new work.</summary>
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static TicketCategory Create(Guid tenantId, string name, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name.Trim(),
        NormalizedName = Normalize(name),
        IsActive = true,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    public void Rename(string name, DateTime nowUtc)
    {
        Name = name.Trim();
        NormalizedName = Normalize(name);
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }
}
