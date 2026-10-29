using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Organizations;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Invitations;

public sealed record InvitationDto(Guid Id, string Email, TenantRole Role, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

public sealed partial class InvitationService(
    IOpsFlowDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IUserDirectory users,
    IEmailService email,
    IOptions<AppOptions> options,
    TimeProvider clock,
    ILogger<InvitationService> logger)
{
    // UPDATED: invitation ki expiry approved requirement ke mutabiq 72 ghante
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>Owners may invite any staff role; Managers may only invite Agents.</summary>
    public async Task<InvitationDto> CreateAsync(string address, TenantRole role, CancellationToken ct)
    {
        if (!role.IsStaff())
        {
            throw AppException.BadRequest("Only staff roles can be invited.");
        }

        if (tenantContext.Role != TenantRole.TenantOwner && role != TenantRole.Agent)
        {
            throw AppException.Forbidden("You can only invite Agents.");
        }

        var tenantId = tenantContext.TenantId!.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var normalized = Invitation.Normalize(address);

        var open = await db.Invitations.Where(i => i.NormalizedEmail == normalized && i.AcceptedAtUtc == null && i.RevokedAtUtc == null).ToListAsync(ct);
        if (open.Exists(i => i.IsPending(now)))
        {
            throw AppException.Conflict("A pending invitation already exists for this email.");
        }

        open.ForEach(i => i.Revoke(now)); // expired leftovers must not block the unique pending index

        var (token, hash) = InvitationToken.Generate();
        var invitation = Invitation.Create(tenantId, address, role, hash, tenantContext.MembershipId!.Value, now, Lifetime);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        LogCreated(logger, invitation.Id, tenantId, role);

        await SendAsync(invitation, token, ct);
        return ToDto(invitation);
    }

    public async Task<IReadOnlyList<InvitationDto>> ListPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.Invitations.Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null).ToListAsync(ct);
        return open.Where(i => i.IsPending(now)).OrderBy(i => i.CreatedAtUtc).Select(ToDto).ToList();
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw AppException.NotFound("Invitation not found.");
        if (invitation.AcceptedAtUtc is not null)
        {
            throw AppException.Conflict("The invitation was already accepted.");
        }

        invitation.Revoke(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Accepts an invitation as the signed-in user. Their verified email must match the invited address.
    /// Retry-safe: the same user repeating (or racing) the request gets the membership they already have.
    /// </summary>
    public async Task<OrganizationDto> AcceptAsync(string token, CancellationToken ct)
    {
        // UPDATED: accept ab idempotent hai - retry ya concurrent request par same user ko existing membership milti hai
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = InvitationToken.Hash(token);
        var userId = currentUser.UserId;
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is not null && invitation.AcceptedByUserId == userId)
        {
            return await ExistingAsync(invitation.TenantId, userId, ct) ?? throw InvalidInvitation();
        }

        if (invitation is null || !invitation.IsPending(now))
        {
            throw InvalidInvitation();
        }

        var user = (await users.GetAsync([userId], ct)).GetValueOrDefault(userId);
        if (user is null || !user.EmailConfirmed || Invitation.Normalize(user.Email) != invitation.NormalizedEmail)
        {
            throw AppException.Forbidden("This invitation was issued to a different email address.");
        }

        using (tenantContext.Begin(invitation.TenantId))
        {
            if (await db.Memberships.AnyAsync(m => m.UserId == userId, ct))
            {
                // Same user already joined (e.g. a racing request won): return that membership instead of failing.
                return await ExistingAsync(invitation.TenantId, userId, ct)
                    ?? throw AppException.Conflict("You are already a member of this organization.");
            }

            invitation.Accept(userId, now);
            db.Memberships.Add(Membership.Create(invitation.TenantId, userId, invitation.Role, now));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent accept by the same user wins the unique (TenantId, UserId) index; treat as success.
                db.ChangeTracker.Clear();
                return await ExistingAsync(invitation.TenantId, userId, ct)
                    ?? throw AppException.Conflict("The invitation could not be accepted.", "It may already have been used.");
            }
        }

        LogAccepted(logger, invitation.Id, invitation.TenantId, userId);
        return (await ExistingAsync(invitation.TenantId, userId, ct))!;
    }

    private async Task<OrganizationDto?> ExistingAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        using var scope = tenantContext.Begin(tenantId);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.Status == MembershipStatus.Active, ct);
        var tenant = membership is null ? null : await db.Tenants.AsNoTracking().SingleOrDefaultAsync(ct);
        return tenant is null ? null : new OrganizationDto(tenant.Id, tenant.Name, tenant.Slug, membership!.Role);
    }

    private static AppException InvalidInvitation() => AppException.BadRequest("The invitation is invalid or has expired.");

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
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The invitation is already saved; revoke and re-invite if delivery keeps failing.
            LogEmailFailed(logger, ex, invitation.Id);
        }
    }

    private static InvitationDto ToDto(Invitation i) => new(i.Id, i.Email, i.Role, i.CreatedAtUtc, i.ExpiresAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invitation {InvitationId} for role {Role} created in tenant {TenantId}")]
    private static partial void LogCreated(ILogger logger, Guid invitationId, Guid tenantId, TenantRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invitation {InvitationId} of tenant {TenantId} accepted by user {UserId}")]
    private static partial void LogAccepted(ILogger logger, Guid invitationId, Guid tenantId, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending invitation email {InvitationId} failed")]
    private static partial void LogEmailFailed(ILogger logger, Exception exception, Guid invitationId);
}
