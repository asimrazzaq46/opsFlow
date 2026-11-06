using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Staff mutations need an open assignment for Agents; viewing a ticket (here: as its creator) is not enough.</summary>
public class TicketWritePermissionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Counts(int Notes, int Messages, int Attachments, int Activities, int Notifications, int Outbox, int Versions);

    private async Task<Counts> CountsAsync(Guid ticketId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return new Counts(
            await db.TicketNotes.IgnoreQueryFilters().CountAsync(),
            await db.TicketMessages.IgnoreQueryFilters().CountAsync(),
            await db.TicketAttachments.IgnoreQueryFilters().CountAsync(),
            await db.TicketActivities.IgnoreQueryFilters().CountAsync(),
            await db.Notifications.IgnoreQueryFilters().CountAsync(),
            await db.OutboxMessages.CountAsync(),
            await db.Tickets.IgnoreQueryFilters().Where(t => t.Id == ticketId).Select(t => t.Version).SingleAsync());
    }

    private static object Body(Guid categoryId) => new { title = "Write guard", description = "d", categoryId };

    /// <summary>An Agent creates a ticket (self-assigned), a second Agent is assigned and the creator is then unassigned: still visible, no longer responsible.</summary>
    private async Task<(TicketWorld W, TicketJson Ticket)> FormerlyAssignedCreatorAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Agent1, Body(w.CategoryId));
        await w.AssignAsync(ticket.Id, w.Agent2Membership);
        (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();
        return (w, await w.GetTicketAsync(w.Manager, ticket.Id));
    }

    [Fact]
    public async Task A_visible_but_unassigned_agent_cannot_edit_chat_note_or_attach_and_nothing_is_written()
    {
        var (w, t) = await FormerlyAssignedCreatorAsync();
        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.GetAsync(w.Url($"tickets/{t.Id}"))).StatusCode); // still the creator: visible
        var before = await CountsAsync(t.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "Hijacked", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendMessageAsync(w.Agent1, t.Id, "Replying anyway")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AddNoteAsync(w.Agent1, t.Id, "internal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.UploadAsync(w.Agent1, t.Id, "a.txt", "hello"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.UploadAsync(w.Agent1, t.Id, "a.txt", "hello"u8.ToArray(), internalOnly: true)).StatusCode);

        Assert.Equal(before, await CountsAsync(t.Id)); // no rows, notifications, outbox events or version bump
        Assert.Equal("Write guard", (await w.GetTicketAsync(w.Manager, t.Id)).Summary.Title);
    }

    [Fact]
    public async Task An_assigned_agent_keeps_every_staff_operation()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);

        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "Edited by assignee", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.SendMessageAsync(w.Agent1, t.Id, "On it")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.AddNoteAsync(w.Agent1, t.Id, "internal")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.UploadAsync(w.Agent1, t.Id, "a.txt", "hello"u8.ToArray())).StatusCode);
        Assert.Equal("InProgress", (await w.MoveAsync(w.Agent1, t.Id, "InProgress")).Status);
    }

    [Fact]
    public async Task An_agent_who_created_the_ticket_is_self_assigned_and_may_work_on_it()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.CreateTicketAsync(w.Agent1, Body(w.CategoryId));

        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "Mine", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.AddNoteAsync(w.Agent1, t.Id, "note")).StatusCode);
    }

    [Fact]
    public async Task Owners_and_managers_manage_tickets_without_being_assigned_and_customers_keep_their_own_actions()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);

        Assert.Equal(HttpStatusCode.OK, (await w.Manager.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "Manager edit", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.SendMessageAsync(w.Owner, t.Id, "Owner reply")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.AddNoteAsync(w.Manager, t.Id, "manager note")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.UploadAsync(w.Owner, t.Id, "a.txt", "hello"u8.ToArray())).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await w.SendMessageAsync(w.Customer, t.Id, "Any news?")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.UploadAsync(w.Customer, t.Id, "c.txt", "hello"u8.ToArray())).StatusCode);
    }

    [Fact]
    public async Task A_suspended_assigned_agent_and_another_tenants_members_cannot_mutate()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        var other = await TicketWorld.CreateAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "x", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Manager.PostAsync(w.Url($"tickets/{t.Id}/notes"), new { content = "x", clientNoteId = Guid.NewGuid() })).StatusCode);

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false, acknowledgeOpenWork = true })).EnsureSuccessStatusCode();
        var before = await CountsAsync(t.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PutAsync(w.Url($"tickets/{t.Id}"), new { title = "x", version = t.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AddNoteAsync(w.Agent1, t.Id, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendMessageAsync(w.Agent1, t.Id, "x")).StatusCode);
        Assert.Equal(before, await CountsAsync(t.Id));
    }

    [Fact]
    public async Task Child_tasks_still_need_an_open_assignment()
    {
        var (w, t) = await FormerlyAssignedCreatorAsync();

        var response = await w.Agent1.PostAsync(w.Url($"tickets/{t.Id}/tasks"), new { title = "Sneaky task" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // visible in the workspace, but its child tasks are for the assignees
    }
}
