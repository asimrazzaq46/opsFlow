using Microsoft.AspNetCore.Mvc;
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

/// <summary>Customer contacts: managed by Owners and Managers only. Deactivation (never deletion) keeps ticket history intact.</summary>
public sealed partial class CustomerService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    TimeProvider clock,
    ILogger<CustomerService> logger
)
{
    private const int MaxPageSize = 100;

    public async Task<PagedResult<CustomerDto>> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        await RequireManagerAsync(ct);
        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize); // overflow-safe paging

        var query = db.Customers.AsNoTracking();
        if (isActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(..) LIKE; culture/StringComparison overloads cannot be translated
            query = query.Where(c =>
                c.Name.ToLower().Contains(term) || c.NormalizedEmail.Contains(term)
            );
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(c => new CustomerDto(
                c.Id,
                c.Name,
                c.Email,
                c.Phone,
                c.UserId,
                c.IsActive,
                c.CreatedAtUtc,
                c.UpdatedAtUtc
            ))
            .ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, page, pageSize, total);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        return ToDto(
            await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
                ?? throw NotFound()
        );
    }

    public async Task<ActionResult<CustomerDto>> CreateAsync(
        string name,
        string email,
        string? phone,
        Guid? linkedUserId,
        CancellationToken ct
    )
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage customers.");
        }

        var normalized = EmailAddress.Normalize(email);
        if (await db.Customers.AnyAsync(c => c.NormalizedEmail == normalized, ct))
        {
            return DuplicateEmail();
        }

        var customer = Customer.Create(TenantId, name, email, phone, Now);
        if (linkedUserId is { } userId)
        {
            if (!await IsEligibleLinkAsync(userId, ct))
            {
                return IneligibleLink();
            }

            customer.LinkUser(userId, Now);
        }

        db.Customers.Add(customer);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        LogCreated(logger, customer.Id, TenantId);
        return ToDto(customer);
    }

    public async Task<ActionResult<CustomerDto>> UpdateAsync(
        Guid id,
        string name,
        string email,
        string? phone,
        Guid? linkedUserId,
        CancellationToken ct
    )
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage customers.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return CustomerNotFound();
        }

        var normalized = EmailAddress.Normalize(email);
        if (await db.Customers.AnyAsync(c => c.Id != id && c.NormalizedEmail == normalized, ct))
        {
            return DuplicateEmail();
        }

        if (
            linkedUserId is { } userId
            && userId != customer.UserId
            && !await IsEligibleLinkAsync(userId, ct)
        )
        {
            return IneligibleLink();
        }

        var now = Now;
        customer.Update(name, email, phone, now);
        customer.LinkUser(linkedUserId, now);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        return ToDto(customer);
    }

    /// <returns>Null when applied; otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage customers.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return CustomerNotFound();
        }

        customer.SetActive(isActive, Now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return null;
    }

    // The linked user must hold an ACTIVE Customer membership in THIS tenant: an email match alone never grants ticket access.
    private Task<bool> IsEligibleLinkAsync(Guid userId, CancellationToken ct) =>
        db.Memberships.AnyAsync(
            m =>
                m.UserId == userId
                && m.Role == TenantRole.Customer
                && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == userId && u.Status == UserStatus.Active),
            ct
        );

    private static BadRequestObjectResult IneligibleLink() =>
        new(
            new ProblemDetails
            {
                Title = "The user must be an active Customer member of this organization.",
            }
        );

    /// <returns>Null when saved; the 409 when a unique rule was violated by a concurrent writer.</returns>
    private async Task<ObjectResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.CustomerEmail))
        {
            return DuplicateEmail();
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.CustomerUser))
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "This user is already linked to another customer." }
            );
        }
    }

    private static ConflictObjectResult DuplicateEmail() =>
        new(new ProblemDetails { Title = "A customer with this email already exists." });

    private static NotFoundObjectResult CustomerNotFound() =>
        new(new ProblemDetails { Title = "Customer not found." });

    private Task RequireManagerAsync(CancellationToken ct) =>
        actor.RequireManagerAsync("Only Owners and Managers can manage customers.", ct);

    private Guid TenantId => actor.TenantId;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static AppException NotFound() => AppException.NotFound("Customer not found.");

    private static CustomerDto ToDto(Customer c) =>
        new(c.Id, c.Name, c.Email, c.Phone, c.UserId, c.IsActive, c.CreatedAtUtc, c.UpdatedAtUtc);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Customer {CustomerId} created in tenant {TenantId}"
    )]
    private static partial void LogCreated(ILogger logger, Guid customerId, Guid tenantId);
}
