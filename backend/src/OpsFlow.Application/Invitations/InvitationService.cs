using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Organizations;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Invitations;

public sealed partial class InvitationService(
    IOpsFlowDbContext db,
    ITenantContext tenantContext,
    ActorAccess actor,
    ICurrentUser currentUser,
    IUserDirectory users,
    IEmailService email,
    IOptions<AppOptions> options,
    TimeProvider clock,
    ILogger<InvitationService> logger
)
{
    private const int MaxAttempts = 3;

    // 72-hour expiry per the approved requirement.
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>Minimum time between two emails for the same invitation.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Permission matrix: Owners manage invitations of any staff role; Managers only Agent invitations.</summary>
    private static bool CanManage(TenantRole actorRole, TenantRole invitedRole) =>
        actorRole == TenantRole.TenantOwner
        || (
            actorRole == TenantRole.Manager
            && invitedRole is TenantRole.Agent or TenantRole.Customer
        );

    /// <summary>
    /// The inviter's role is read from the database NOW (membership + user must be active), the tenant stamp is read first and
    /// rotated on commit, so an inviter who loses authorization during the request cannot commit the invitation.
    /// </summary>
    public async Task<ActionResult<InvitationDto>> CreateAsync(
        string address,
        TenantRole role,
        CancellationToken ct
    )
    {
        var tenantId = tenantContext.TenantId!.Value;
        var normalized = Invitation.Normalize(address);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Current DB authorization, not a role snapshot; the stamp is read first and rotated on commit.
            var tenant = await db.Tenants.SingleAsync(ct);
            if (await actor.GetActiveRoleAsync(ct) is not { } actorRole)
            {
                return ActorAccess.AccessNoLongerValid();
            }

            if (!CanManage(actorRole, role))
            {
                return ActorAccess.Forbidden(
                    actorRole == TenantRole.Manager
                        ? "You can only invite Agents and Customers."
                        : "You are not allowed to invite members."
                );
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var open = await db
                .Invitations.Where(i =>
                    i.NormalizedEmail == normalized
                    && i.AcceptedAtUtc == null
                    && i.RevokedAtUtc == null
                )
                .ToListAsync(ct);
            if (open.Exists(i => i.IsPending(now)))
            {
                return PendingInvitationExists();
            }

            open.ForEach(i => i.Revoke(now)); // expired leftovers must not block the unique pending index

            var (token, hash) = SecureToken.Generate();
            var invitation = Invitation.Create(
                tenantId,
                address,
                role,
                hash,
                tenantContext.MembershipId!.Value,
                now,
                Lifetime
            );
            db.Invitations.Add(invitation);
            tenant.RotateOwnerChangeStamp();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.OpenInvitation))
            {
                return PendingInvitationExists();
            }

            LogCreated(logger, invitation.Id, tenantId, role);
            await SendAsync(invitation, token, ct);
            return ToDto(invitation);
        }

        return ChangedConcurrently();
    }

    public async Task<IReadOnlyList<InvitationDto>> ListPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db
            .Invitations.Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
            .ToListAsync(ct);
        return open.Where(i => i.IsPending(now))
            .OrderBy(i => i.CreatedAtUtc)
            .Select(ToDto)
            .ToList();
    }

    /// <returns>Null when the invitation was revoked; otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> RevokeAsync(Guid invitationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Revoke follows the same current-authorization + stamp protocol (Managers only Agent invitations).
            var tenant = await db.Tenants.SingleAsync(ct);
            if (await actor.GetActiveRoleAsync(ct) is not { } actorRole)
            {
                return ActorAccess.AccessNoLongerValid();
            }

            var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId, ct);
            if (invitation is null)
            {
                return new NotFoundObjectResult(new ProblemDetails { Title = "Invitation not found." });
            }

            if (!CanManage(actorRole, invitation.Role))
            {
                return ActorAccess.Forbidden("You can only revoke Agent invitations.");
            }

            if (invitation.AcceptedAtUtc is not null)
            {
                return new ConflictObjectResult(
                    new ProblemDetails { Title = "The invitation was already accepted." }
                );
            }

            invitation.Revoke(clock.GetUtcNow().UtcDateTime);
            tenant.RotateOwnerChangeStamp();
            try
            {
                await db.SaveChangesAsync(ct);
                return null;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        return ChangedConcurrently();
    }

    /// <summary>
    /// Re-sends a pending or expired (but not accepted/revoked) invitation with a NEW token and a fresh 72-hour expiry; role, email and
    /// tenant never change. The old token stops working in the same commit. Follows the same current-authorization + OwnerChangeStamp
    /// protocol as create/revoke, so two concurrent resends cannot both commit: the loser retries and then hits the cooldown. Like
    /// invitation creation, the email carries a one-time secret and is therefore sent directly (best effort) after the commit, never
    /// through the durable outbox; if delivery fails the invitation can be resent again after the cooldown.
    /// </summary>
    public async Task<ActionResult<InvitationDto>> ResendAsync(Guid invitationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var tenant = await db.Tenants.SingleAsync(ct);
            if (await actor.GetActiveRoleAsync(ct) is not { } actorRole)
            {
                return ActorAccess.AccessNoLongerValid();
            }

            var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invitationId, ct);
            if (invitation is null)
            {
                return new NotFoundObjectResult(new ProblemDetails { Title = "Invitation not found." });
            }

            if (!CanManage(actorRole, invitation.Role))
            {
                return ActorAccess.Forbidden("You can only resend Agent and Customer invitations.");
            }

            if (!invitation.IsOpen)
            {
                return new ConflictObjectResult(
                    new ProblemDetails
                    {
                        Title = "The invitation was already accepted or revoked.",
                        Extensions = { ["code"] = "INVITATION_NOT_RESENDABLE" },
                    }
                );
            }

            if (
                await db.Memberships.AnyAsync(
                    m =>
                        m.Status == MembershipStatus.Active
                        && db.Users.Any(u => u.Id == m.UserId && u.NormalizedEmail == invitation.NormalizedEmail),
                    ct
                )
            )
            {
                return new ConflictObjectResult(
                    new ProblemDetails
                    {
                        Title = "This person is already an active member of the organization.",
                        Extensions = { ["code"] = "INVITATION_ALREADY_MEMBER" },
                    }
                );
            }

            var now = clock.GetUtcNow().UtcDateTime;
            // The last send time is derived from the fixed lifetime, so no extra column is needed.
            var lastSentAt = invitation.ExpiresAtUtc - Lifetime;
            if (now - lastSentAt < ResendCooldown)
            {
                return new ObjectResult(
                    new ProblemDetails
                    {
                        Title = "The invitation was sent moments ago.",
                        Detail = $"Wait {ResendCooldown.TotalMinutes:0} minutes before resending it.",
                        Extensions = { ["code"] = "INVITATION_RESEND_COOLDOWN" },
                    }
                )
                {
                    StatusCode = StatusCodes.Status429TooManyRequests,
                };
            }

            var (token, hash) = SecureToken.Generate();
            await using (var transaction = await db.BeginTransactionAsync(ct))
            {
                // Compare-and-swap on the token that was read: a concurrent accept, revoke or resend makes this match no row,
                // and the retry then reports the current state (not resendable or cooldown) without sending an email.
                var swapped = await db
                    .Invitations.Where(i =>
                        i.Id == invitation.Id
                        && i.TokenHash == invitation.TokenHash
                        && i.AcceptedAtUtc == null
                        && i.RevokedAtUtc == null
                    )
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(i => i.TokenHash, hash).SetProperty(i => i.ExpiresAtUtc, now + Lifetime),
                        ct
                    );
                if (swapped == 0)
                {
                    db.ChangeTracker.Clear();
                    continue;
                }

                tenant.RotateOwnerChangeStamp();
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    db.ChangeTracker.Clear();
                    continue; // disposing the transaction rolls the token swap back
                }

                await transaction.CommitAsync(ct);
            }

            invitation.Reissue(hash, now, Lifetime); // untracked copy: reflects the committed values for the email and response
            LogResent(logger, invitation.Id, tenant.Id);
            await SendAsync(invitation, token, ct);
            return ToDto(invitation);
        }

        return ChangedConcurrently();
    }

    /// <summary>
    /// Accepts an invitation as the signed-in user. Their verified email must match the invited address.
    /// Retry-safe: the same user repeating (or racing) the request gets the membership they already have.
    /// </summary>
    public async Task<ActionResult<OrganizationDto>> AcceptAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = SecureToken.Hash(token);
        var userId = currentUser.UserId;
        var invitation = await db
            .Invitations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is not null && invitation.AcceptedByUserId == userId)
        {
            return await ExistingAsync(invitation.TenantId, userId, ct) is { } repeated
                ? repeated
                : InvalidInvitation();
        }

        if (invitation is null || !invitation.IsPending(now))
        {
            return InvalidInvitation();
        }

        var user = (await users.GetAsync([userId], ct)).GetValueOrDefault(userId);
        if (
            user is null
            || !user.EmailConfirmed
            || Invitation.Normalize(user.Email) != invitation.NormalizedEmail
        )
        {
            return ActorAccess.Forbidden("This invitation was issued to a different email address.");
        }

        // The inviter must still be authorized; after demotion/suspension/deactivation an old invitation no longer works.
        if (!await InviterStillAuthorizedAsync(invitation, ct))
        {
            return InvalidInvitation();
        }

        using (tenantContext.Begin(invitation.TenantId))
        {
            if (await db.Memberships.AnyAsync(m => m.UserId == userId, ct))
            {
                // An existing member keeps their membership (role unchanged) and the invitation is consumed.
                var existing = await ExistingAsync(invitation.TenantId, userId, ct);
                if (existing is null)
                {
                    return new ConflictObjectResult(
                        new ProblemDetails { Title = "You are already a member of this organization." }
                    );
                }

                await db
                    .Invitations.Where(i =>
                        i.Id == invitation.Id
                        && i.TokenHash == hash
                        && i.AcceptedAtUtc == null
                        && i.RevokedAtUtc == null
                    )
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAtUtc, now), ct);
                return existing;
            }

            await using var transaction = await db.BeginTransactionAsync(ct);
            // Conditional claim: only succeeds while THIS token is still current and pending, so a concurrent resend, revoke
            // or accept cannot both succeed with it.
            var claimed = await db
                .Invitations.Where(i =>
                    i.Id == invitation.Id
                    && i.TokenHash == hash
                    && i.AcceptedAtUtc == null
                    && i.RevokedAtUtc == null
                    && i.ExpiresAtUtc > now
                )
                .ExecuteUpdateAsync(
                    s => s.SetProperty(i => i.AcceptedAtUtc, now).SetProperty(i => i.AcceptedByUserId, userId),
                    ct
                );
            if (claimed == 0)
            {
                await transaction.RollbackAsync(ct);
                var acceptedBy = await db
                    .Invitations.AsNoTracking()
                    .Where(i => i.Id == invitation.Id)
                    .Select(i => i.AcceptedByUserId)
                    .SingleAsync(ct);
                // The same user's concurrent request won: idempotent success, as for a repeated accept.
                if (acceptedBy != userId)
                {
                    return InvalidInvitation();
                }

                return await ExistingAsync(invitation.TenantId, userId, ct) is { } won
                    ? won
                    : InvalidInvitation();
            }

            db.Memberships.Add(
                Membership.Create(invitation.TenantId, userId, invitation.Role, now)
            );
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.MembershipUser))
            {
                // The user became a member concurrently; the claim is rolled back and the existing membership returned.
                db.ChangeTracker.Clear();
                await transaction.RollbackAsync(ct);
                return await ExistingAsync(invitation.TenantId, userId, ct) is { } existingMember
                    ? existingMember
                    : new ConflictObjectResult(
                        new ProblemDetails
                        {
                            Title = "The invitation could not be accepted.",
                            Detail = "It may already have been used.",
                        }
                    );
            }
        }

        LogAccepted(logger, invitation.Id, invitation.TenantId, userId);
        return (await ExistingAsync(invitation.TenantId, userId, ct))!;
    }

    // The composite FK guarantees the inviter belongs to the same tenant; here we check the inviter is STILL entitled.
    private async Task<bool> InviterStillAuthorizedAsync(
        Invitation invitation,
        CancellationToken ct
    )
    {
        var inviterRole = await db
            .Memberships.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m =>
                m.Id == invitation.InvitedByMembershipId
                && m.TenantId == invitation.TenantId
                && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
            )
            .Select(m => (TenantRole?)m.Role)
            .SingleOrDefaultAsync(ct);
        return inviterRole is { } role && CanManage(role, invitation.Role);
    }

    private async Task<OrganizationDto?> ExistingAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct
    )
    {
        using var scope = tenantContext.Begin(tenantId);
        var membership = await db
            .Memberships.AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.UserId == userId && m.Status == MembershipStatus.Active,
                ct
            );
        var tenant = membership is null
            ? null
            : await db.Tenants.AsNoTracking().SingleOrDefaultAsync(ct);
        return tenant is null
            ? null
            : new OrganizationDto(tenant.Id, tenant.Name, tenant.Slug, membership!.Role);
    }

    private static BadRequestObjectResult InvalidInvitation() =>
        new(new ProblemDetails { Title = "The invitation is invalid or has expired." });

    private static ConflictObjectResult PendingInvitationExists() =>
        new(new ProblemDetails { Title = "A pending invitation already exists for this email." });

    private static ConflictObjectResult ChangedConcurrently() =>
        new(
            new ProblemDetails
            {
                Title = "The organization changed concurrently.",
                Detail = "Please retry.",
            }
        );

    private async Task SendAsync(Invitation invitation, string token, CancellationToken ct)
    {
        try
        {
            var tenant = await db.Tenants.SingleAsync(ct);
            var link = $"{options.Value.BaseUrl.TrimEnd('/')}/invitations/accept?token={token}";
            await email.SendAsync(
                invitation.Email,
                $"You are invited to join {tenant.Name} on OpsFlow",
                $"You have been invited to join {tenant.Name} as {invitation.Role}.\n\nAccept the invitation: {link}\n\nThis link expires on {invitation.ExpiresAtUtc:u}.",
                ct
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The invitation is already saved; revoke and re-invite if delivery keeps failing.
            LogEmailFailed(logger, ex, invitation.Id);
        }
    }

    private static InvitationDto ToDto(Invitation i) =>
        new(i.Id, i.Email, i.Role, i.CreatedAtUtc, i.ExpiresAtUtc);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} for role {Role} created in tenant {TenantId}"
    )]
    private static partial void LogCreated(
        ILogger logger,
        Guid invitationId,
        Guid tenantId,
        TenantRole role
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} of tenant {TenantId} resent with a new token"
    )]
    private static partial void LogResent(ILogger logger, Guid invitationId, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} of tenant {TenantId} accepted by user {UserId}"
    )]
    private static partial void LogAccepted(
        ILogger logger,
        Guid invitationId,
        Guid tenantId,
        Guid userId
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Sending invitation email {InvitationId} failed"
    )]
    private static partial void LogEmailFailed(
        ILogger logger,
        Exception exception,
        Guid invitationId
    );
}
