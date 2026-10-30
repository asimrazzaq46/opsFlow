using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Tenant provisioning, ticket creation, visibility, updates, list/search/sort/paging, numbering and the activity timeline.</summary>
public class TicketTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] CustomerVisibleTypes = ["Created", "StatusChanged", "Resolved", "Reopened"];

    // ---------------------------------------------------------------- provisioning

    [Fact]
    public async Task New_tenants_get_their_defaults_atomically_with_the_tenant()
    {
        var w = await TicketWorld.CreateAsync(factory);

        var categories = await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(w.Url("ticket-categories")));
        var policies = await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(w.Url("sla-policies")));
        var first = await w.CreateTicketAsync(w.Manager);

        Assert.Equal(["Billing", "General", "Technical Issue"], categories.Select(c => c.Name).Order().ToArray());
        Assert.Equal(["Low", "Medium", "High", "Urgent"], policies.Select(p => p.Priority).ToArray());
        Assert.Equal(1, first.Summary.Number);
        Assert.Equal("TKT-00001", first.Summary.DisplayNumber);
    }

    [Fact]
    public async Task Initialization_of_an_older_tenant_is_explicit_idempotent_and_never_duplicates()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var counterOnly = await TicketWorld.CreateAsync(factory);
        using (var counterScope = factory.Services.CreateScope())
        {
            await counterScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().TicketCounters.IgnoreQueryFilters().Where(c => c.TenantId == counterOnly.Org.Id).ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await counterOnly.TryCreateTicketAsync(counterOnly.Manager, new { title = "No numbering", description = "d", categoryId = counterOnly.CategoryId })).StatusCode);
        await StripDefaultsAsync(w);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, new { title = "No categories", description = "d", categoryId = w.CategoryId })).StatusCode);

        // only the Owner may initialize; three concurrent attempts must still produce exactly one set of defaults
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Manager.PostAsync(w.Url("defaults"))).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => w.Owner.PostAsync(w.Url("defaults"))));
        var again = await w.Owner.ReadAsync<DefaultsResultJson>(await w.Owner.PostAsync(w.Url("defaults")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(0, again.RecordsAdded);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(3, await db.TicketCategories.IgnoreQueryFilters().CountAsync(c => c.TenantId == w.Org.Id));
        Assert.Equal(4, await db.SlaPolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.Org.Id));
        Assert.Equal(1, await db.Settings.IgnoreQueryFilters().CountAsync(s => s.TenantId == w.Org.Id));
        Assert.Equal(1, await db.TicketCounters.IgnoreQueryFilters().CountAsync(s => s.TenantId == w.Org.Id));
        var category = (await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(w.Url("ticket-categories")))).First(c => c.Name == "General");
        Assert.Equal(1, (await w.CreateTicketAsync(w.Manager, new { title = "Now it works", description = "d", categoryId = category.Id })).Summary.Number);
    }

    private sealed record DefaultsResultJson(int RecordsAdded);

    private async Task StripDefaultsAsync(TicketWorld w)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        await db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == w.Org.Id).ExecuteDeleteAsync();
        await db.TicketCategories.IgnoreQueryFilters().Where(c => c.TenantId == w.Org.Id).ExecuteDeleteAsync();
        await db.Settings.IgnoreQueryFilters().Where(s => s.TenantId == w.Org.Id).ExecuteDeleteAsync();
        await db.TicketCounters.IgnoreQueryFilters().Where(s => s.TenantId == w.Org.Id).ExecuteDeleteAsync();
    }

    // ---------------------------------------------------------------- creation + requester rules

    [Fact]
    public async Task A_customer_creates_a_real_unassigned_ticket_with_an_initial_sla_cycle_and_timeline()
    {
        var w = await TicketWorld.CreateAsync(factory);

        var ticket = await w.CreateTicketAsync(w.Customer);

        Assert.Equal("Open", ticket.Status);
        Assert.Equal(w.CustomerContactId, ticket.Summary.CustomerId);
        Assert.Equal(w.Customer.UserId, ticket.CreatedByUserId); // requester derived from the authenticated identity
        Assert.Equal("Medium", ticket.Summary.Priority);
        Assert.Null(ticket.Assignees); // staffing and SLA are staff-only
        Assert.Null(ticket.CurrentSla);
        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal(1, sla.Current!.CycleNumber);
        Assert.Equal("Initial", sla.Current.Reason);
        Assert.Equal(sla.Current.StartedAtUtc.AddMinutes(480), sla.Current.ResponseDueAtUtc); // Medium: 480 / 2880 minutes
        Assert.Equal(sla.Current.StartedAtUtc.AddMinutes(2880), sla.Current.ResolutionDueAtUtc);
        Assert.Equal(["Created", "SlaCycleStarted"], (await w.ActivityAsync(w.Manager, ticket.Id)).Select(a => a.Type).ToArray());
    }

    [Fact]
    public async Task A_customer_cannot_substitute_the_customer_priority_or_assignees()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var other = await w.CreateCustomerAsync();
        var basic = new { title = "Something broke", description = "d", categoryId = w.CategoryId };

        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Customer, new { basic.title, basic.description, basic.categoryId, customerId = other.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Customer, new { basic.title, basic.description, basic.categoryId, priority = "Urgent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Customer, new { basic.title, basic.description, basic.categoryId, assigneeMembershipIds = new[] { w.Agent1Membership } })).StatusCode);
    }

    [Fact]
    public async Task An_agent_creates_a_self_assigned_ticket_and_cannot_create_on_behalf_of_others()
    {
        var w = await TicketWorld.CreateAsync(factory);

        var ticket = await w.CreateTicketAsync(w.Agent1, new { title = "My own task", description = "d", categoryId = w.CategoryId, priority = "High" });
        var mine = await w.Agent1.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Agent1.GetAsync(w.Url("tickets")));

        Assert.Equal("Assigned", ticket.Status);
        Assert.Null(ticket.Summary.CustomerId);
        Assert.Equal(w.Agent1Membership, Assert.Single(ticket.Assignees!).MembershipId);
        Assert.Single(mine.Items, t => t.Id == ticket.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Agent1, new { title = "For a customer", description = "d", categoryId = w.CategoryId, customerId = w.CustomerContactId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Agent1, new { title = "For a colleague", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent2Membership } })).StatusCode);
    }

    [Fact]
    public async Task A_manager_creates_for_an_active_customer_and_can_assign_agents_at_creation()
    {
        var w = await TicketWorld.CreateAsync(factory);

        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Phone order", description = "d", categoryId = w.CategoryId, customerId = w.CustomerContactId, priority = "Urgent", assigneeMembershipIds = new[] { w.Agent1Membership, w.Agent2Membership } });

        Assert.Equal("Assigned", ticket.Status);
        Assert.Equal(2, ticket.Assignees!.Count);
        Assert.Equal(w.Manager.UserId, ticket.CreatedByUserId);
        Assert.Equal(w.CustomerContactId, ticket.Summary.CustomerId);
        Assert.Equal(["Created", "SlaCycleStarted", "AgentAssigned", "AgentAssigned", "StatusChanged"], (await w.ActivityAsync(w.Owner, ticket.Id)).Select(a => a.Type).ToArray());
    }

    [Fact]
    public async Task Invalid_references_are_rejected_server_side()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var other = await TicketWorld.CreateAsync(factory);
        var inactive = await w.Owner.ReadAsync<CategoryJson>(await w.Owner.PostAsync(w.Url("ticket-categories"), new { name = "Retired" }));
        (await w.Owner.PutAsync(w.Url($"ticket-categories/{inactive.Id}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        object Body(Guid category, Guid[]? agents = null, string priority = "Low") => new { title = "Check refs", description = "d", categoryId = category, priority, assigneeMembershipIds = agents };

        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(inactive.Id))).StatusCode); // inactive category
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(Guid.NewGuid()))).StatusCode); // unknown category
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(other.CategoryId))).StatusCode); // another tenant's category
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(w.CategoryId, [w.Agent2Membership]))).StatusCode); // suspended agent
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(w.CategoryId, [other.Agent1Membership]))).StatusCode); // agent of another tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, Body(w.CategoryId, [w.ManagerMembership]))).StatusCode); // not an Agent
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, new { title = "x", description = "d", categoryId = w.CategoryId })).StatusCode); // title too short
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, new { title = "Bad priority", description = "d", categoryId = w.CategoryId, priority = "Critical" })).StatusCode);
    }

    // ---------------------------------------------------------------- numbering

    [Fact]
    public async Task Concurrent_creation_yields_unique_gapless_numbers_per_tenant()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);

        var inA = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => a.TryCreateTicketAsync(a.Manager, new { title = "Parallel", description = "d", categoryId = a.CategoryId })));
        var bTicket = await b.CreateTicketAsync(b.Manager);

        Assert.All(inA, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = (await Task.WhenAll(inA.Select(async r => (await r.Content.ReadFromJsonAsync<TicketJson>())!.Summary.Number))).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => (long)n).ToArray(), numbers);
        Assert.Equal(1, bTicket.Summary.Number); // independent sequence per tenant
    }

    [Fact]
    public async Task A_failed_creation_rolls_the_number_back_without_a_gap()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var first = await w.CreateTicketAsync(w.Manager);
        factory.Faults.FailNextSave();

        var failed = await w.TryCreateTicketAsync(w.Manager, new { title = "Doomed", description = "d", categoryId = w.CategoryId });
        var next = await w.CreateTicketAsync(w.Manager);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(first.Summary.Number + 1, next.Summary.Number);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(2, await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == w.Org.Id)); // no ticket, cycle or timeline of the failed attempt
        Assert.Equal(2, await db.TicketSlaCycles.IgnoreQueryFilters().CountAsync(t => t.TenantId == w.Org.Id));
    }

    // ---------------------------------------------------------------- visibility

    [Fact]
    public async Task Visibility_follows_the_role_and_never_the_tenant_alone()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var otherCustomerUser = await ApiClient.AddMemberAsync(factory, w.Owner, w.Org.Id, "Customer");
        await w.CreateCustomerAsync(otherCustomerUser.UserId, "Other Buyer");
        var customerTicket = await w.CreateTicketAsync(w.Customer);
        var otherCustomerTicket = await w.CreateTicketAsync(otherCustomerUser);
        var agentOwn = await w.CreateTicketAsync(w.Agent1, new { title = "Agent one task", description = "d", categoryId = w.CategoryId });
        var agent2Own = await w.CreateTicketAsync(w.Agent2, new { title = "Agent two task", description = "d", categoryId = w.CategoryId });
        await w.AssignAsync(customerTicket.Id, w.Agent1Membership);

        async Task<Guid[]> IdsAsync(ApiClient who) =>
            (await who.ReadAsync<PagedDto<TicketSummaryJson>>(await who.GetAsync(w.Url("tickets?pageSize=100")))).Items.Select(t => t.Id).Order().ToArray();
        static Guid[] Expect(params Guid[] ids) => ids.Order().ToArray();

        Assert.Equal(Expect(customerTicket.Id), await IdsAsync(w.Customer));
        Assert.Equal(Expect(otherCustomerTicket.Id), await IdsAsync(otherCustomerUser));
        Assert.Equal(Expect(customerTicket.Id, agentOwn.Id), await IdsAsync(w.Agent1)); // assigned + self-created, not the colleague's
        Assert.Equal(Expect(agent2Own.Id), await IdsAsync(w.Agent2));
        Assert.Equal(4, (await IdsAsync(w.Manager)).Length);
        Assert.Equal(4, (await IdsAsync(w.Owner)).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Customer.GetAsync(w.Url($"tickets/{otherCustomerTicket.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Agent2.GetAsync(w.Url($"tickets/{agentOwn.Id}"))).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_access_and_multi_organization_users_stay_isolated()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticketInA = await a.CreateTicketAsync(a.Customer);
        var ticketInB = await b.CreateTicketAsync(b.Customer);

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url($"tickets/{ticketInA.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync(b.Url($"tickets/{ticketInA.Id}"))).StatusCode); // id substitution inside the caller's tenant
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.PostAsync(b.Url($"tickets/{ticketInA.Id}/reopen"), new { })).StatusCode);

        // one person: Manager in A and Customer in B (same access token, role decided per tenant)
        var both = await ApiClient.AddMemberAsync(factory, a.Owner, a.Org.Id, "Manager");
        (await b.Owner.PostAsync(b.Url("invitations"), new { email = both.Email, role = "Customer" })).EnsureSuccessStatusCode();
        (await both.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, both.Email) })).EnsureSuccessStatusCode();
        await b.Owner.PutAsync(b.Url($"customers/{b.CustomerContactId}"), new { name = "Relinked", email = $"re-{Guid.NewGuid():N}@example.com", linkedUserId = both.UserId });

        Assert.Single((await both.ReadAsync<PagedDto<TicketSummaryJson>>(await both.GetAsync(b.Url("tickets")))).Items, t => t.Id == ticketInB.Id);
        Assert.Contains((await both.ReadAsync<PagedDto<TicketSummaryJson>>(await both.GetAsync(a.Url("tickets")))).Items, t => t.Id == ticketInA.Id); // manager in A sees all of A
        Assert.Equal(HttpStatusCode.Forbidden, (await both.GetAsync(b.Url("customers"))).StatusCode); // but is only a customer in B
    }

    [Fact]
    public async Task Role_and_membership_changes_affect_ticket_access_immediately()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        Assert.Equal(HttpStatusCode.OK, (await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}"))).StatusCode);

        (await w.Owner.PutAsync(w.Url($"members/{w.ManagerMembership}/role"), new { role = "Agent" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}"))).StatusCode); // an Agent sees only assigned/own tickets

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url("tickets"))).StatusCode);
    }

    // ---------------------------------------------------------------- update

    [Fact]
    public async Task Protected_fields_cannot_be_changed_through_update()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        var response = await w.Manager.PutAsync(w.Url($"tickets/{ticket.Id}"), new
        {
            title = "Renamed",
            status = "Closed",
            number = 999,
            tenantId = Guid.NewGuid(),
            createdByUserId = w.Manager.UserId,
            customerId = Guid.NewGuid(),
        });

        var after = await w.GetTicketAsync(w.Manager, ticket.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", after.Summary.Title);
        Assert.Equal("Open", after.Status);
        Assert.Equal(ticket.Summary.Number, after.Summary.Number);
        Assert.Equal(ticket.CreatedByUserId, after.CreatedByUserId);
        Assert.Equal(ticket.Summary.CustomerId, after.Summary.CustomerId);
    }

    [Fact]
    public async Task Update_permissions_and_concurrency_are_enforced()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        Assert.Equal(HttpStatusCode.OK, (await w.Customer.PutAsync(w.Url($"tickets/{ticket.Id}"), new { title = "Customer edit", version = ticket.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PutAsync(w.Url($"tickets/{ticket.Id}"), new { priority = "Urgent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PutAsync(w.Url($"tickets/{ticket.Id}"), new { title = "Stale", version = ticket.Version })).StatusCode); // version moved on
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PutAsync(w.Url($"tickets/{ticket.Id}"), new { title = "Too late" })).StatusCode); // no longer Open
        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.PutAsync(w.Url($"tickets/{ticket.Id}"), new { priority = "High" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Agent2.PutAsync(w.Url($"tickets/{ticket.Id}"), new { priority = "Low" })).StatusCode); // not their ticket
    }

    // ---------------------------------------------------------------- list / search / sort / paging

    [Fact]
    public async Task List_supports_filtering_searching_sorting_and_stable_paging()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var urgent = await w.CreateTicketAsync(w.Manager, new { title = "Server down", description = "d", categoryId = w.CategoryId, priority = "Urgent", customerId = w.CustomerContactId });
        var high = await w.CreateTicketAsync(w.Manager, new { title = "Invoice question", description = "d", categoryId = w.CategoryId, priority = "High" });
        var low1 = await w.CreateTicketAsync(w.Manager, new { title = "Printer toner", description = "d", categoryId = w.CategoryId, priority = "Low" });
        var low2 = await w.CreateTicketAsync(w.Manager, new { title = "Printer paper", description = "d", categoryId = w.CategoryId, priority = "Low" });
        await w.AssignAsync(high.Id, w.Agent1Membership);
        await w.MoveAsync(w.Agent1, high.Id, "InProgress");

        async Task<PagedDto<TicketSummaryJson>> ListAsync(string query) =>
            await w.Manager.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Manager.GetAsync(w.Url("tickets" + query)));

        Assert.Equal([high.Id], (await ListAsync("?status=InProgress")).Items.Select(t => t.Id).ToArray());
        Assert.Equal(4, (await ListAsync("?status=InProgress&status=Open")).TotalCount); // repeated status parameters
        Assert.Equal([low1.Id, low2.Id], (await ListAsync("?priority=Low&sortBy=number")).Items.Select(t => t.Id).ToArray());
        Assert.Equal([urgent.Id], (await ListAsync($"?customerId={w.CustomerContactId}")).Items.Select(t => t.Id).ToArray());
        Assert.Equal([high.Id], (await ListAsync($"?assignedMembershipId={w.Agent1Membership}")).Items.Select(t => t.Id).ToArray());
        Assert.Equal(2, (await ListAsync("?search=PRINTER")).TotalCount); // case-insensitive title search
        Assert.Equal([low1.Id], (await ListAsync($"?search={low1.Summary.DisplayNumber}")).Items.Select(t => t.Id).ToArray()); // by ticket number
        Assert.Equal([low1.Id], (await ListAsync($"?search={low1.Summary.Number}")).Items.Select(t => t.Id).ToArray()); // bare number
        Assert.Equal([urgent.Id, high.Id, low1.Id, low2.Id], (await ListAsync("?sortBy=priority&descending=true")).Items.Select(t => t.Id).ToArray()); // number breaks the Low tie
        Assert.Equal(4, (await ListAsync($"?createdFrom={DateTime.UtcNow.AddDays(-1):O}&createdTo={DateTime.UtcNow.AddDays(1):O}")).TotalCount);

        var page1 = await ListAsync("?sortBy=number&pageSize=3");
        var page2 = await ListAsync("?sortBy=number&pageSize=3&page=2");
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(3, page1.Items.Count);
        Assert.Equal([low2.Id], page2.Items.Select(t => t.Id).ToArray());
        Assert.Equal(100, (await ListAsync("?pageSize=100000")).PageSize); // bounded
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.GetAsync(w.Url("tickets?sortBy=password"))).StatusCode); // whitelisted sort fields only
    }

    [Fact]
    public async Task Customers_cannot_use_staff_only_filters()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Customer);

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url($"tickets?assignedMembershipId={w.Agent1Membership}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url($"tickets?customerId={w.CustomerContactId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url("tickets?state=Overdue"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Customer.GetAsync(w.Url("tickets?state=Open&search=printer"))).StatusCode);
    }

    [Fact]
    public async Task The_overdue_filter_uses_persisted_deadlines_and_the_current_time()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var urgent = await w.CreateTicketAsync(w.Manager, new { title = "Fast one", description = "d", categoryId = w.CategoryId, priority = "Urgent" });
        var low = await w.CreateTicketAsync(w.Manager, new { title = "Slow one", description = "d", categoryId = w.CategoryId, priority = "Low" });
        Assert.Equal(0, (await w.Manager.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Manager.GetAsync(w.Url("tickets?state=Overdue")))).TotalCount);

        await w.AdvanceAsync(TimeSpan.FromHours(9)); // Urgent resolves within 480 minutes, Low within 4320

        var overdue = await w.Manager.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Manager.GetAsync(w.Url("tickets?state=Overdue")));
        Assert.Equal([urgent.Id], overdue.Items.Select(t => t.Id).ToArray());
        Assert.True(overdue.Items.Single().IsOverdue);
        Assert.False((await w.GetTicketAsync(w.Manager, low.Id)).Summary.IsOverdue);
    }

    // ---------------------------------------------------------------- activity

    [Fact]
    public async Task The_timeline_is_ordered_paginated_and_hides_internal_events_from_customers()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");
        (await w.Agent1.PutAsync(w.Url($"tickets/{ticket.Id}"), new { priority = "High", description = "secret internal detail" })).EnsureSuccessStatusCode();

        var staff = await w.ActivityAsync(w.Manager, ticket.Id);
        var customer = await w.ActivityAsync(w.Customer, ticket.Id);
        var paged = await w.Manager.ReadAsync<PagedDto<ActivityJson>>(await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}/activity?pageSize=2&page=2")));

        Assert.Equal(staff.Select(a => a.Sequence).Order().ToArray(), staff.Select(a => a.Sequence).ToArray()); // chronological
        Assert.Equal(Enumerable.Range(1, staff.Count).ToArray(), staff.Select(a => a.Sequence).ToArray());
        Assert.Contains(staff, a => a.Type == "AgentAssigned" && a.ActorUserId == w.Manager.UserId);
        Assert.Contains(staff, a => a.Type == "PriorityChanged" && a.OldValue == "Medium" && a.NewValue == "High");
        Assert.DoesNotContain(staff, a => (a.NewValue ?? string.Empty).Contains("secret", StringComparison.OrdinalIgnoreCase)); // content never copied into history
        Assert.All(customer, a => Assert.Contains(a.Type, CustomerVisibleTypes));
        Assert.All(customer, a => Assert.Null(a.ActorUserId));
        Assert.Contains(customer, a => a.Type == "StatusChanged" && a.NewValue == "Assigned");
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal(staff.Count, paged.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Agent2.GetAsync(w.Url($"tickets/{ticket.Id}/activity"))).StatusCode);
    }

    [Fact]
    public async Task Rejected_operations_create_no_activity_and_failures_roll_back_together()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        var before = (await w.ActivityAsync(w.Manager, ticket.Id)).Count;

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "Closed" })).StatusCode); // invalid transition
        Assert.Equal(HttpStatusCode.NotFound, (await w.Agent2.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress" })).StatusCode); // not visible to them
        Assert.Equal(before, (await w.ActivityAsync(w.Manager, ticket.Id)).Count);

        factory.Faults.FailNextSave();
        Assert.Equal(HttpStatusCode.InternalServerError, (await w.Agent1.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress" })).StatusCode);

        var after = await w.GetTicketAsync(w.Manager, ticket.Id);
        Assert.Equal("Assigned", after.Status); // the status change and its activity rolled back together
        Assert.Equal(before, (await w.ActivityAsync(w.Manager, ticket.Id)).Count);
    }
}
