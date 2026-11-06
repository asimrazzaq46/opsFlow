using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Employee administration: open-work protection, stale updates, audit, skills permissions and directory privacy.</summary>
public class TeamTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> GraphQlAsync(TicketWorld w, ApiClient who, string query)
    {
        var response = await who.PostAsync(w.Url("graphql"), new { query });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<Guid> CreateSkillAsync(TicketWorld w, ApiClient who, string name)
    {
        var response = await who.PostAsync(w.Url("skills"), new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task An_agent_with_open_work_cannot_be_moved_to_another_role_until_it_is_reassigned()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);

        var refused = await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/role"), new { role = "Manager", expectedRole = "Agent" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
        {
            Assert.Equal("MEMBER_HAS_OPEN_WORK", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(1, problem.RootElement.GetProperty("openAssignmentCount").GetInt32());
            Assert.Contains(problem.RootElement.GetProperty("tickets").EnumerateArray(), t => t.GetProperty("ticketId").GetGuid() == ticket.Id);
        }

        // Nothing changed: the member is still an Agent and no audit entry was written for the refused attempt.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            Assert.Equal("Agent", (await db.Memberships.IgnoreQueryFilters().SingleAsync(m => m.Id == w.Agent1Membership)).Role.ToString());
            Assert.False(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.TargetMembershipId == w.Agent1Membership));
        }

        (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/role"), new { role = "Manager", expectedRole = "Agent" })).StatusCode
        );

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var audit = await db.AuditEvents.IgnoreQueryFilters().SingleAsync(e => e.TargetMembershipId == w.Agent1Membership);
            Assert.Equal(w.Owner.UserId, audit.ActorUserId);
            Assert.Equal(w.Org.Id, audit.TenantId);
        }

        // A second administrator acting on the stale screen ("still an Agent") is refused instead of overwriting.
        var stale = await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false, expectedRole = "Agent" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Suspending_an_agent_with_open_work_needs_an_explicit_acknowledgement_and_then_applies_immediately()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent2Membership);

        var first = await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        using (var problem = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            Assert.Equal("MEMBER_HAS_OPEN_WORK", problem.RootElement.GetProperty("code").GetString());
            Assert.True(problem.RootElement.GetProperty("canAcknowledge").GetBoolean());
            Assert.Contains(problem.RootElement.GetProperty("tickets").EnumerateArray(), t => t.GetProperty("ticketId").GetGuid() == ticket.Id);
        }

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false, acknowledgeOpenWork = true })).StatusCode
        );
        // A role change can never be acknowledged away: the work must be reassigned first.
        var promote = await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/role"), new { role = "Manager", expectedRole = "Agent" });
        Assert.Equal(HttpStatusCode.Conflict, promote.StatusCode);
    }

    [Fact]
    public async Task Agents_never_receive_colleagues_emails_through_the_rest_member_list()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using var doc = JsonDocument.Parse(await (await w.Agent1.GetAsync(w.Url("members?pageSize=50"))).Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var mine = row.GetProperty("id").GetGuid() == w.Agent1Membership;
            Assert.Equal(mine ? JsonValueKind.String : JsonValueKind.Null, row.GetProperty("email").ValueKind);
        }
    }

    [Fact]
    public async Task Skills_are_managed_by_owners_for_everyone_and_by_managers_for_agents_only()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var dotnet = await CreateSkillAsync(w, w.Manager, "Networking");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsync(w.Url("skills"), new { name = " networking " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(w.Url("skills"), new { name = "Security" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await w.Manager.PutAsync(w.Url($"members/{w.Agent1Membership}/skills"), new { skillIds = new[] { dotnet } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Manager.PutAsync(w.Url($"members/{w.ManagerMembership}/skills"), new { skillIds = new[] { dotnet } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PutAsync(w.Url($"members/{w.Agent1Membership}/skills"), new { skillIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PutAsync(w.Url($"members/{w.Agent1Membership}/profile"), new { jobTitle = "Lead" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PutAsync(w.Url($"members/{w.ManagerMembership}/profile"), new { jobTitle = "Support lead" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PutAsync(w.Url($"members/{w.Agent1Membership}/skills"), new { skillIds = Array.Empty<Guid>() })).StatusCode);

        // Another workspace can neither use this skill nor reach this member.
        var other = await TicketWorld.CreateAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Owner.PutAsync(other.Url($"members/{other.Agent1Membership}/skills"), new { skillIds = new[] { dotnet } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PutAsync(other.Url($"members/{w.Agent1Membership}/skills"), new { skillIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task The_directory_hides_other_peoples_emails_from_agents_and_is_closed_to_customers()
    {
        var w = await TicketWorld.CreateAsync(factory);
        const string Query = "{ teamDirectory(pageSize: 50) { items { membershipId email } } }";

        var asAgent = await GraphQlAsync(w, w.Agent1, Query);
        foreach (var row in asAgent.GetProperty("data").GetProperty("teamDirectory").GetProperty("items").EnumerateArray())
        {
            var mine = row.GetProperty("membershipId").GetGuid() == w.Agent1Membership;
            Assert.Equal(mine ? JsonValueKind.String : JsonValueKind.Null, row.GetProperty("email").ValueKind);
        }

        var asOwner = await GraphQlAsync(w, w.Owner, Query);
        Assert.All(
            asOwner.GetProperty("data").GetProperty("teamDirectory").GetProperty("items").EnumerateArray(),
            row => Assert.Equal(JsonValueKind.String, row.GetProperty("email").ValueKind)
        );

        var asCustomer = await GraphQlAsync(w, w.Customer, Query);
        Assert.True(asCustomer.TryGetProperty("errors", out _));

        // Agents open only their own detail.
        var other = await GraphQlAsync(w, w.Agent1, $"{{ teamMember(membershipId: \"{w.Agent2Membership}\") {{ stats {{ resolveActions }} }} }}");
        Assert.True(other.TryGetProperty("errors", out _));
        var self = await GraphQlAsync(w, w.Agent1, $"{{ teamMember(membershipId: \"{w.Agent1Membership}\") {{ canEditProfileAndSkills stats {{ resolveActions }} }} }}");
        Assert.False(self.GetProperty("data").GetProperty("teamMember").GetProperty("canEditProfileAndSkills").GetBoolean());
    }
}
