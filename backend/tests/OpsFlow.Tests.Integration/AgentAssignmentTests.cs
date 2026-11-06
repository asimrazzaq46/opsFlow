using System.Net;
using System.Text.Json;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Agents may ADD assignments (claim or a teammate); removal, other tenants, inactive members and customers stay out.</summary>
public class AgentAssignmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Assignments(TicketWorld w, Guid ticketId) => w.Url($"tickets/{ticketId}/assignments");

    private async Task<(TicketWorld W, TicketJson Ticket)> OpenTicketAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        return (w, await w.CreateTicketAsync(w.Customer));
    }

    [Fact]
    public async Task An_unassigned_agent_can_claim_a_ticket_and_existing_assignees_stay()
    {
        var (w, t) = await OpenTicketAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);

        var response = await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent1Membership } });
        var claimed = await w.Agent1.ReadAsync<TicketJson>(response);

        Assert.True(claimed.Summary.AssignedToMe);
        Assert.Equal(new[] { w.Agent1Membership, w.Agent2Membership }.Order(), claimed.Assignees!.Select(a => a.MembershipId).Order());
        Assert.Equal(HttpStatusCode.Created, (await w.SendMessageAsync(w.Agent1, t.Id, "now mine")).StatusCode); // the claim unlocks the assignee's operations
    }

    [Fact]
    public async Task Claiming_an_open_ticket_moves_it_to_assigned_and_repeated_or_concurrent_claims_never_duplicate()
    {
        var (w, t) = await OpenTicketAsync();
        Assert.Equal("Open", t.Status);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent1Membership } })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var after = await w.GetTicketAsync(w.Manager, t.Id);
        Assert.Equal("Assigned", after.Status);
        Assert.Single(after.Assignees!);
    }

    [Fact]
    public async Task An_agent_can_add_a_teammate_without_being_assigned_and_without_assigning_themselves()
    {
        var (w, t) = await OpenTicketAsync();

        var updated = await w.Agent1.ReadAsync<TicketJson>(await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent2Membership } }));

        Assert.Equal(new[] { w.Agent2Membership }, updated.Assignees!.Select(a => a.MembershipId));
        Assert.False(updated.Summary.AssignedToMe); // adding a teammate gives the actor no responsibility
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AddNoteAsync(w.Agent1, t.Id, "still not mine")).StatusCode);
    }

    [Fact]
    public async Task Agents_cannot_remove_assignees_or_assign_ineligible_members_and_customers_cannot_assign()
    {
        var (w, t) = await OpenTicketAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.DeleteAsync($"{Assignments(w, t.Id)}/{w.Agent2Membership}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.ManagerMembership } })).StatusCode); // not an Agent
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { Guid.NewGuid() } })).StatusCode); // unknown
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode);

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false, acknowledgeOpenWork = true })).EnsureSuccessStatusCode();
        var inactive = await w.Agent1.PostAsync(Assignments(w, (await w.CreateTicketAsync(w.Customer)).Id), new { membershipIds = new[] { w.Agent2Membership } });
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode); // suspended members are not assignable

        var other = await TicketWorld.CreateAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { other.Agent1Membership } })).StatusCode); // another workspace
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { other.Agent1Membership } })).StatusCode); // no membership here
        Assert.Equal(new[] { w.Agent2Membership }, (await w.GetTicketAsync(w.Manager, t.Id)).Assignees!.Select(a => a.MembershipId));
    }

    [Fact]
    public async Task Finished_tickets_cannot_be_claimed_and_a_claim_unlocks_child_task_creation_only_for_the_claimer()
    {
        var (w, t) = await OpenTicketAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(w.Url($"tickets/{t.Id}/tasks"), new { title = "before the claim" })).StatusCode);

        (await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent1Membership } })).EnsureSuccessStatusCode();
        var created = await w.Agent1.PostAsync(w.Url($"tickets/{t.Id}/tasks"), new { title = "after the claim" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(w.Agent1Membership.ToString(), await created.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase); // the task is the claimer's own

        await w.MoveAsync(w.Agent1, t.Id, "Resolved");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Agent1.PostAsync(Assignments(w, t.Id), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode);
    }

    [Fact]
    public async Task Board_cards_carry_their_assignees_for_staff_and_never_for_customers()
    {
        var (w, t) = await OpenTicketAsync();
        await w.AssignAsync(t.Id, w.Agent1Membership, w.Agent2Membership);

        var staff = await w.Agent2.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ kanbanBoard { tickets { id assignees { membershipId displayName } } } }" });
        var body = JsonDocument.Parse(await staff.Content.ReadAsStringAsync()).RootElement;
        var card = body.GetProperty("data").GetProperty("kanbanBoard").EnumerateArray().SelectMany(c => c.GetProperty("tickets").EnumerateArray()).Single(x => x.GetProperty("id").GetGuid() == t.Id);
        Assert.Equal(2, card.GetProperty("assignees").GetArrayLength());

        var customer = await w.Customer.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ tickets { items { id assignees { membershipId } } } }" });
        var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync()).RootElement;
        Assert.All(customerBody.GetProperty("data").GetProperty("tickets").GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("assignees").ValueKind));
    }
}
