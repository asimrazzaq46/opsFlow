using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Active staff discover every ticket of their workspace; responsibility-sensitive content and every mutation stay with the assignees.</summary>
public class WorkspaceVisibilityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> GqlAsync(ApiClient who, Guid tenantId, string query)
    {
        var response = await who.PostAsync($"/api/v1/tenants/{tenantId}/graphql", new { query });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("errors", out var e) && e.GetArrayLength() > 0 && e[0].TryGetProperty("extensions", out var x) && x.TryGetProperty("code", out var c) ? c.GetString() : null;

    private async Task<(TicketWorld W, TicketJson Mine, TicketJson Others)> WorldAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var mine = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        var others = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent2Membership);
        return (w, mine, others);
    }

    [Fact]
    public async Task An_agent_discovers_every_ticket_of_the_workspace_and_assigned_to_me_stays_personal()
    {
        var (w, mine, others) = await WorldAsync();

        var all = await GqlAsync(w.Agent1, w.Org.Id, "{ tickets(filter: { pageSize: 50 }) { totalCount items { id assignedToMe } } }");
        var items = all.GetProperty("data").GetProperty("tickets").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("assignedToMe").GetBoolean());
        Assert.Equal(2, all.GetProperty("data").GetProperty("tickets").GetProperty("totalCount").GetInt32());
        Assert.True(items[mine.Id]);
        Assert.False(items[others.Id]); // visible, but not the caller's responsibility

        var onlyMine = await GqlAsync(w.Agent1, w.Org.Id, "{ tickets(filter: { assignedToMe: true }) { totalCount items { id } } }");
        Assert.Equal(1, onlyMine.GetProperty("data").GetProperty("tickets").GetProperty("totalCount").GetInt32());
        Assert.Equal(mine.Id, onlyMine.GetProperty("data").GetProperty("tickets").GetProperty("items")[0].GetProperty("id").GetGuid());

        var board = await GqlAsync(w.Agent1, w.Org.Id, "{ kanbanBoard(filter: { assignedToMe: true }) { status total } }");
        Assert.Equal(1, board.GetProperty("data").GetProperty("kanbanBoard").EnumerateArray().Sum(c => c.GetProperty("total").GetInt32()));
        var boardAll = await GqlAsync(w.Agent1, w.Org.Id, "{ kanbanBoard { status total } }");
        Assert.Equal(2, boardAll.GetProperty("data").GetProperty("kanbanBoard").EnumerateArray().Sum(c => c.GetProperty("total").GetInt32()));

        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.GetAsync(w.Url($"tickets/{others.Id}"))).StatusCode);
    }

    [Fact]
    public async Task Sensitive_parts_of_a_ticket_stay_closed_to_an_agent_who_is_not_assigned()
    {
        var (w, _, others) = await WorldAsync();
        await w.SendMessageAsync(w.Agent2, others.Id, "customer-facing reply");
        await w.AddNoteAsync(w.Agent2, others.Id, "internal secret");
        await w.UploadAsync(w.Agent2, others.Id, "a.txt", "hello"u8.ToArray());
        var file = (await w.Agent2.ReadAsync<List<AttachmentJson>>(await w.Agent2.GetAsync(w.Url($"tickets/{others.Id}/attachments")))).Single();

        foreach (var url in new[] { $"tickets/{others.Id}/messages", $"tickets/{others.Id}/notes", $"tickets/{others.Id}/attachments", $"tickets/{others.Id}/attachments/{file.Id}", $"tickets/{others.Id}/tasks" })
        {
            var response = await w.Agent1.GetAsync(w.Url(url));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("internal secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("customer-facing reply", body, StringComparison.Ordinal);
        }

        var detail = await w.Agent1.GetAsync(w.Url($"tickets/{others.Id}"));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode); // a useful read-only overview
        Assert.DoesNotContain("internal secret", await detail.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await w.Agent2.GetAsync(w.Url($"tickets/{others.Id}/notes"))).StatusCode); // the assignee still has all of it
        Assert.Equal(HttpStatusCode.OK, (await w.Manager.GetAsync(w.Url($"tickets/{others.Id}/messages"))).StatusCode);
    }

    [Fact]
    public async Task Suspended_agents_and_other_tenants_discover_nothing_and_customers_keep_their_own_view()
    {
        var (w, mine, others) = await WorldAsync();
        var otherTenant = await TicketWorld.CreateAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.Agent1.GetAsync(w.Url($"tickets/{others.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.Agent1.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ tickets { totalCount } }" })).StatusCode);

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url($"tickets/{others.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url("tickets"))).StatusCode);

        var customer = await GqlAsync(w.Customer, w.Org.Id, "{ tickets(filter: { pageSize: 50 }) { totalCount items { id assignedToMe } } }");
        Assert.Equal(2, customer.GetProperty("data").GetProperty("tickets").GetProperty("totalCount").GetInt32()); // both were raised by this customer's contact
        Assert.All(customer.GetProperty("data").GetProperty("tickets").GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("assignedToMe").ValueKind));
        var denied = await GqlAsync(w.Customer, w.Org.Id, "{ tickets(filter: { assignedToMe: true }) { totalCount } }");
        Assert.Equal("FORBIDDEN", Code(denied));
        Assert.NotEqual(Guid.Empty, mine.Id);
    }

    [Fact]
    public async Task Every_active_staff_member_gets_a_content_free_change_event_but_not_the_workspace_customers()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        await factory.DrainOutboxAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlow.Infrastructure.Persistence.OpsFlowDbContext>();
        var events = db.OutboxMessages.Where(m => m.EventType == "ticket.changed").ToList();
        Assert.Single(events, e => e.AggregateId == ticket.Id);
        Assert.DoesNotContain("title", events.Single(e => e.AggregateId == ticket.Id).Payload, StringComparison.OrdinalIgnoreCase); // ids and the kind of change only
    }
}
