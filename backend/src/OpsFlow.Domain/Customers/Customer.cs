using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Customers;

/// <summary>
/// Tenant-owned customer contact. It is NOT an account: an optional, explicitly linked User (with an active Customer
/// membership) is what lets a signed-in person see the tickets of this contact. An email match never grants access.
/// </summary>
public sealed class Customer : ITenantOwned
{
    private Customer()
    {
        Name = string.Empty;
        Email = string.Empty;
        NormalizedEmail = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string Email { get; private set; }
    public string NormalizedEmail { get; private set; }
    public string? Phone { get; private set; }
    public Guid? UserId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Customer Create(
        Guid tenantId,
        string name,
        string email,
        string? phone,
        DateTime nowUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Email = email.Trim(),
            NormalizedEmail = EmailAddress.Normalize(email),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            IsActive = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    public void Update(string name, string email, string? phone, DateTime nowUtc)
    {
        Name = name.Trim();
        Email = email.Trim();
        NormalizedEmail = EmailAddress.Normalize(email);
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        UpdatedAtUtc = nowUtc;
    }

    public void LinkUser(Guid? userId, DateTime nowUtc)
    {
        UserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }
}
