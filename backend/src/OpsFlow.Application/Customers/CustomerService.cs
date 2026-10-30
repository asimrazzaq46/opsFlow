using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Customers;

public sealed record CustomerDto(Guid Id, string Name, string Email, string? Phone, Guid? LinkedUserId, bool IsActive, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

/// <summary>Customer contacts: managed by Owners and Managers only. Deactivation (never deletion) keeps ticket history intact.</summary>
public sealed partial class CustomerService(IOpsFlowDbContext db, ActorAccess actor, TimeProvider clock, ILogger<CustomerService> logger)
{
    private const int MaxPageSize = 100;

    public async Task<PagedResult<CustomerDto>> ListAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Customers.AsNoTracking();
        if (isActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(..) LIKE; culture/StringComparison overloads cannot be translated
            query = query.Where(c => c.Name.ToLower().Contains(term) || c.NormalizedEmail.Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CustomerDto(c.Id, c.Name, c.Email, c.Phone, c.UserId, c.IsActive, c.CreatedAtUtc, c.UpdatedAtUtc)).ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, page, pageSize, total);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        return ToDto(await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound());
    }

    public async Task<CustomerDto> CreateAsync(string name, string email, string? phone, Guid? linkedUserId, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var normalized = EmailAddress.Normalize(email);
        if (await db.Customers.AnyAsync(c => c.NormalizedEmail == normalized, ct))
        {
            throw AppException.Conflict("A customer with this email already exists.");
        }

        var customer = Customer.Create(TenantId, name, email, phone, Now);
        if (linkedUserId is { } userId)
        {
            await ValidateLinkAsync(userId, ct);
            customer.LinkUser(userId, Now);
        }

        db.Customers.Add(customer);
        await SaveAsync(ct);
        LogCreated(logger, customer.Id, TenantId);
        return ToDto(customer);
    }

    public async Task<CustomerDto> UpdateAsync(Guid id, string name, string email, string? phone, Guid? linkedUserId, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        var normalized = EmailAddress.Normalize(email);
        if (await db.Customers.AnyAsync(c => c.Id != id && c.NormalizedEmail == normalized, ct))
        {
            throw AppException.Conflict("A customer with this email already exists.");
        }

        if (linkedUserId is { } userId && userId != customer.UserId)
        {
            await ValidateLinkAsync(userId, ct);
        }

        var now = Now;
        customer.Update(name, email, phone, now);
        customer.LinkUser(linkedUserId, now);
        await SaveAsync(ct);
        return ToDto(customer);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        customer.SetActive(isActive, Now);
        await db.SaveChangesAsync(ct);
    }

    // The linked user must hold an ACTIVE Customer membership in THIS tenant: an email match alone never grants ticket access.
    private async Task ValidateLinkAsync(Guid userId, CancellationToken ct)
    {
        var eligible = await db.Memberships.AnyAsync(
            m => m.UserId == userId && m.Role == TenantRole.Customer && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == userId && u.Status == UserStatus.Active), ct);
        if (!eligible)
        {
            throw AppException.BadRequest("The user must be an active Customer member of this organization.");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.CustomerEmail))
        {
            throw AppException.Conflict("A customer with this email already exists.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.CustomerUser))
        {
            throw AppException.Conflict("This user is already linked to another customer.");
        }
    }

    private async Task RequireManagerAsync(CancellationToken ct)
    {
        if (await actor.RequireActiveRoleAsync(ct) is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            throw AppException.Forbidden("Only Owners and Managers can manage customers.");
        }
    }

    private Guid TenantId => actor.TenantId;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static AppException NotFound() => AppException.NotFound("Customer not found.");

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Name, c.Email, c.Phone, c.UserId, c.IsActive, c.CreatedAtUtc, c.UpdatedAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {CustomerId} created in tenant {TenantId}")]
    private static partial void LogCreated(ILogger logger, Guid customerId, Guid tenantId);
}
