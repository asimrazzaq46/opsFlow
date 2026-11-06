using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Customers;

/// <summary>Customer contacts: managed by Owners and Managers only. Deactivation (never deletion) keeps ticket history intact.</summary>
public sealed partial class CustomerService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    OutboxWriter outbox,
    TimeProvider clock,
    ILogger<CustomerService> logger
)
{
    private const int MaxPageSize = 100;
    private const int MaxLinkablePageSize = 25;

    public async Task<PagedResult<CustomerDto>> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken ct,
        string? sortBy = null,
        bool descending = false
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
        // Whitelisted sort keys with the Id tiebreaker keep paging stable.
        var ordered = (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("email", false) => query.OrderBy(c => c.NormalizedEmail),
            ("email", true) => query.OrderByDescending(c => c.NormalizedEmail),
            ("created", false) => query.OrderBy(c => c.CreatedAtUtc),
            ("created", true) => query.OrderByDescending(c => c.CreatedAtUtc),
            (_, true) => query.OrderByDescending(c => c.Name),
            _ => query.OrderBy(c => c.Name),
        };
        var items = await Project(ordered.ThenBy(c => c.Id).Skip(skip).Take(pageSize)).ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, page, pageSize, total);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        return await Project(db.Customers.AsNoTracking().Where(c => c.Id == id)).SingleOrDefaultAsync(ct)
            ?? throw NotFound();
    }

    /// <summary>
    /// Active Customer members of THIS tenant (membership query filter) that have no contact yet, for the link picker.
    /// Bounded to a small page: managers narrow it with the search term.
    /// </summary>
    public async Task<IReadOnlyList<LinkableUserDto>> LinkableUsersAsync(string? search, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var query =
            from m in db.Memberships.AsNoTracking()
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            where
                m.Role == TenantRole.Customer
                && m.Status == MembershipStatus.Active
                && u.Status == UserStatus.Active
                && !db.Customers.Any(c => c.UserId == u.Id)
            select new { u.Id, u.Email, u.NormalizedEmail, u.DisplayName };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(..) LIKE
            query = query.Where(u => u.NormalizedEmail.Contains(term) || u.DisplayName.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        return await query
            .OrderBy(u => u.NormalizedEmail)
            .Take(MaxLinkablePageSize)
            .Select(u => new LinkableUserDto(u.Id, u.Email, u.DisplayName))
            .ToListAsync(ct);
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
            outbox.StageCustomerEligibilityChanged(TenantId, customer.Id, userId);
        }

        db.Customers.Add(customer);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        LogCreated(logger, customer.Id, TenantId);
        return await GetProjectedAsync(customer.Id, ct);
    }

    public async Task<ActionResult<CustomerDto>> UpdateAsync(
        Guid id,
        string name,
        string? email,
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

        // Contact details belong to the account owner: a differing value is refused instead of being ignored silently.
        if (
            (email is not null && EmailAddress.Normalize(email) != customer.NormalizedEmail)
            || (!string.IsNullOrWhiteSpace(phone) && phone.Trim() != customer.Phone)
        )
        {
            return ActorAccess.Forbidden("Only the account owner can change their email address or phone number.");
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
        customer.Rename(name, now);
        if (linkedUserId is { } newUser && newUser != customer.UserId)
        {
            if (customer.UserId is { } previous)
            {
                outbox.StageCustomerEligibilityChanged(TenantId, customer.Id, previous);
            }

            customer.LinkUser(newUser, now); // null keeps the current link; only UnlinkUserAsync removes it
            outbox.StageCustomerEligibilityChanged(TenantId, customer.Id, newUser);
        }

        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        return await GetProjectedAsync(id, ct);
    }

    /// <summary>Removes the portal-user link only; the contact and its ticket history stay. Idempotent.</summary>
    /// <returns>Null when applied; otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> UnlinkUserAsync(Guid id, CancellationToken ct)
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

        if (customer.UserId is { } linked)
        {
            customer.LinkUser(null, Now);
            outbox.StageCustomerEligibilityChanged(TenantId, customer.Id, linked);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return null;
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
        if (role != TenantRole.TenantOwner)
        {
            return ActorAccess.Forbidden("Only Owners can stop or allow new tickets for a customer.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return CustomerNotFound();
        }

        if (customer.IsActive != isActive)
        {
            customer.SetActive(isActive, Now);
            if (customer.UserId is { } portalUser)
            {
                outbox.StageCustomerEligibilityChanged(TenantId, customer.Id, portalUser);
            }
        }

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

    private Task<CustomerDto> GetProjectedAsync(Guid id, CancellationToken ct) =>
        Project(db.Customers.AsNoTracking().Where(c => c.Id == id)).SingleAsync(ct);

    // Users are a global table without a tenant filter: only the id already linked to a tenant contact is looked up.
    private IQueryable<CustomerDto> Project(IQueryable<Customer> query) =>
        query.Select(c => new CustomerDto(
            c.Id,
            c.Name,
            c.Email,
            c.Phone,
            c.UserId,
            c.IsActive,
            c.CreatedAtUtc,
            c.UpdatedAtUtc,
            db.Users.Where(u => u.Id == c.UserId).Select(u => u.Email).FirstOrDefault(),
            db.Users.Where(u => u.Id == c.UserId).Select(u => u.DisplayName).FirstOrDefault()
        ));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Customer {CustomerId} created in tenant {TenantId}"
    )]
    private static partial void LogCreated(ILogger logger, Guid customerId, Guid tenantId);
}
