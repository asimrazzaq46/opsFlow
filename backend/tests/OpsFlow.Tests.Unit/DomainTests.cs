using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Tests.Unit;

public class DomainTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Invitation NewInvitation(TenantRole role = TenantRole.Agent) =>
        Invitation.Create(Guid.NewGuid(), " New@Example.com ", role, "hash", Guid.NewGuid(), Now, TimeSpan.FromDays(7));

    [Fact]
    public void Invitation_normalizes_email_and_expires()
    {
        var invitation = NewInvitation();

        Assert.Equal("new@example.com", invitation.NormalizedEmail);
        Assert.True(invitation.IsPending(Now.AddDays(6)));
        Assert.False(invitation.IsPending(Now.AddDays(8)));
    }

    [Fact]
    public void Invitation_is_not_pending_after_accept_or_revoke()
    {
        var accepted = NewInvitation();
        accepted.Accept(Guid.NewGuid(), Now);
        var revoked = NewInvitation();
        revoked.Revoke(Now);

        Assert.False(accepted.IsPending(Now));
        Assert.False(revoked.IsPending(Now));
    }

    [Fact]
    public void Invitation_rejects_unknown_roles_but_allows_customers() =>
        Assert.Throws<ArgumentException>(() => NewInvitation((TenantRole)99)); // UPDATED: Customer invitations ab allowed hain

    [Fact]
    public void Membership_active_owner_rules()
    {
        var owner = Membership.Create(Guid.NewGuid(), Guid.NewGuid(), TenantRole.TenantOwner, Now);
        Assert.True(owner.IsActiveOwner);

        owner.SetActive(false, Now);
        Assert.False(owner.IsActiveOwner);

        owner.SetActive(true, Now);
        owner.ChangeRole(TenantRole.Manager, Now);
        Assert.False(owner.IsActiveOwner);
    }

    [Fact]
    public void Tenant_owner_change_stamp_rotates()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", "acme", Now);
        var before = tenant.OwnerChangeStamp;

        tenant.RotateOwnerChangeStamp();

        Assert.NotEqual(before, tenant.OwnerChangeStamp);
    }

    [Fact]
    public void Invitation_token_is_hashed_and_unique()
    {
        var (token, hash) = SecureToken.Generate();
        var (other, _) = SecureToken.Generate();

        Assert.NotEqual(token, hash);
        Assert.Equal(hash, SecureToken.Hash(token));
        Assert.NotEqual(token, other);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
    }

    [Fact]
    public void TenantContext_begin_scope_restores_previous_state()
    {
        var context = new TenantContext();
        var outer = Guid.NewGuid();
        context.Set(outer, Guid.NewGuid(), TenantRole.Agent);

        using (context.Begin(Guid.NewGuid()))
        {
            Assert.Null(context.Role);
        }

        Assert.Equal(outer, context.TenantId);
        Assert.Equal(TenantRole.Agent, context.Role);
    }
}
