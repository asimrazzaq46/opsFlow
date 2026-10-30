using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Tests.Integration.Support;

public sealed record CategoryJson(Guid Id, string Name, bool IsActive);
public sealed record CustomerJson(Guid Id, string Name, string Email, string? Phone, Guid? LinkedUserId, bool IsActive);
public sealed record SlaPolicyJson(Guid Id, string Priority, int ResponseMinutes, int ResolutionMinutes, bool IsActive);

public sealed record TicketSummaryJson(
    Guid Id, long Number, string DisplayNumber, string Title, string Priority, string Status, Guid CategoryId, string CategoryName,
    Guid? CustomerId, string? CustomerName, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? ResolvedAtUtc, int Version,
    int? AssigneeCount, int? EligibleAssigneeCount, DateTime? ResolutionDueAtUtc, bool? IsOverdue);

public sealed record AssigneeJson(Guid MembershipId, Guid UserId, string DisplayName, DateTime AssignedAtUtc, bool IsAgentActive);

public sealed record SlaCycleJson(
    Guid Id, int CycleNumber, string Reason, DateTime StartedAtUtc, int ResponseMinutes, int ResolutionMinutes, DateTime? ResponseDueAtUtc,
    DateTime ResolutionDueAtUtc, DateTime? FirstResponseAtUtc, DateTime? ResolvedAtUtc, string ResponseState, string ResolutionState, double? RemainingResolutionMinutes);

public sealed record TicketJson(TicketSummaryJson Summary, string Description, Guid? CreatedByUserId, List<AssigneeJson>? Assignees, SlaCycleJson? CurrentSla)
{
    public Guid Id => Summary.Id;
    public string Status => Summary.Status;
    public int Version => Summary.Version;
}

public sealed record TicketSlaJson(SlaCycleJson? Current, List<SlaCycleJson> Cycles);
public sealed record ActivityJson(Guid Id, int Sequence, string Type, DateTime OccurredAtUtc, Guid? ActorUserId, string? OldValue, string? NewValue);
public sealed record AssignmentJson(Guid Id, Guid MembershipId, Guid UserId, string DisplayName, DateTime AssignedAtUtc, Guid AssignedByMembershipId, DateTime? UnassignedAtUtc, Guid? UnassignedByMembershipId, bool IsActive);

/// <summary>One fully populated tenant: Owner, Manager, two Agents, a linked Customer user + contact, defaults provisioned by onboarding.</summary>
public sealed class TicketWorld
{
    private readonly List<ApiClient> _clients = [];

    public required ApiFactory Factory { get; init; }
    public required OrgDto Org { get; init; }
    public required ApiClient Owner { get; init; }
    public required ApiClient Manager { get; init; }
    public required ApiClient Agent1 { get; init; }
    public required ApiClient Agent2 { get; init; }
    public required ApiClient Customer { get; init; }
    public required Guid OwnerMembership { get; init; }
    public required Guid ManagerMembership { get; init; }
    public required Guid Agent1Membership { get; init; }
    public required Guid Agent2Membership { get; init; }
    public Guid CustomerContactId { get; private set; }
    public Guid CategoryId { get; private set; }

    public string Url(string tail) => $"/api/v1/tenants/{Org.Id}/{tail}";

    public static async Task<TicketWorld> CreateAsync(ApiFactory factory)
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync($"World {Guid.NewGuid():N}");
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var agent1 = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Agent");
        var agent2 = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Agent");
        var customer = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Customer");
        var members = await owner.MembersAsync(org.Id);
        var world = new TicketWorld
        {
            Factory = factory,
            Org = org,
            Owner = owner,
            Manager = manager,
            Agent1 = agent1,
            Agent2 = agent2,
            Customer = customer,
            OwnerMembership = members.Single(m => m.UserId == owner.UserId).Id,
            ManagerMembership = members.Single(m => m.UserId == manager.UserId).Id,
            Agent1Membership = members.Single(m => m.UserId == agent1.UserId).Id,
            Agent2Membership = members.Single(m => m.UserId == agent2.UserId).Id,
        };
        world._clients.AddRange([owner, manager, agent1, agent2, customer]);
        world.CategoryId = (await owner.ReadAsync<List<CategoryJson>>(await owner.GetAsync(world.Url("ticket-categories")))).Single(c => c.Name == "General").Id;
        world.CustomerContactId = (await world.CreateCustomerAsync(customer.UserId)).Id;
        return world;
    }

    public async Task<CustomerJson> CreateCustomerAsync(Guid? linkedUserId = null, string? name = null) =>
        await Owner.ReadAsync<CustomerJson>(await Owner.PostAsync(Url("customers"),
            new { name = name ?? "Acme Buyer", email = $"buyer-{Guid.NewGuid():N}@example.com", phone = "+39 02 1234567", linkedUserId }));

    public async Task<TicketJson> CreateTicketAsync(ApiClient who, object? body = null) =>
        await who.ReadAsync<TicketJson>(await who.PostAsync(Url("tickets"), body ?? new { title = "Printer is broken", description = "It prints blank pages.", categoryId = CategoryId }));

    public Task<HttpResponseMessage> TryCreateTicketAsync(ApiClient who, object body) => who.PostAsync(Url("tickets"), body);

    public async Task<TicketJson> GetTicketAsync(ApiClient who, Guid id) => await who.ReadAsync<TicketJson>(await who.GetAsync(Url($"tickets/{id}")));

    public async Task<TicketJson> AssignAsync(Guid ticketId, params Guid[] membershipIds) =>
        await Manager.ReadAsync<TicketJson>(await Manager.PostAsync(Url($"tickets/{ticketId}/assignments"), new { membershipIds }));

    public async Task<TicketJson> MoveAsync(ApiClient who, Guid ticketId, string status) =>
        await who.ReadAsync<TicketJson>(await who.PostAsync(Url($"tickets/{ticketId}/status"), new { status }));

    public async Task<List<ActivityJson>> ActivityAsync(ApiClient who, Guid ticketId) =>
        (await who.ReadAsync<PagedDto<ActivityJson>>(await who.GetAsync(Url($"tickets/{ticketId}/activity?pageSize=100")))).Items;

    public async Task<TicketSlaJson> SlaAsync(Guid ticketId) => await Manager.ReadAsync<TicketSlaJson>(await Manager.GetAsync(Url($"tickets/{ticketId}/sla")));

    /// <summary>Advances the test clock and refreshes everybody's access token (they only live 15 minutes).</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Factory.Clock.Advance(by);
        foreach (var client in _clients)
        {
            (await client.RefreshAsync()).EnsureSuccessStatusCode();
        }
    }

    // ---- PostgreSQL lock choreography (deterministic: hold an UNCOMMITTED change, observe the request blocking on it, then commit) ----

    /// <summary>Starts a transaction on its own connection and applies the update WITHOUT committing; the caller commits or disposes it.</summary>
    public async Task<(IServiceScope Scope, IDbContextTransaction Transaction)> HoldUncommittedAsync(string updateSql)
    {
        var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(updateSql);
        return (scope, transaction);
    }

    public string SuspendMembershipSql(Guid membershipId) => $"UPDATE \"Memberships\" SET \"Status\" = 'Inactive' WHERE \"Id\" = '{membershipId}'";

    public string DemoteMembershipSql(Guid membershipId) => $"UPDATE \"Memberships\" SET \"Role\" = 'Agent' WHERE \"Id\" = '{membershipId}'";

    public string DeactivateUserSql(Guid userId) => $"UPDATE \"Users\" SET \"Status\" = 'Deactivated' WHERE \"Id\" = '{userId}'";

    /// <summary>Bounded wait until some backend is blocked on a row lock (replaces sleeping and hoping).</summary>
    public async Task WaitForLockWaiterAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()").SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No request ever blocked on the row lock.");
    }

    // ---- service-level access for deterministic interleaving tests (same DI scope a request would get) ----

    public IServiceScope ScopeAs(ApiClient client, Guid membershipId, TenantRole role)
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(Org.Id, membershipId, role);
        return scope;
    }

    /// <summary>Makes the current async flow "be" this user for ICurrentUser (call it inside the Task that runs the service).</summary>
    public static void SignInAs(IServiceProvider services, ApiClient client)
    {
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", client.UserId.ToString()), new Claim("sid", Guid.NewGuid().ToString())], "test")),
        };
    }
}
