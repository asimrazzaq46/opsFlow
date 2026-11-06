using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Ticket chat, internal notes, notifications and attachments: persistence, visibility, idempotency and tenant isolation.</summary>
public class ConversationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(TicketWorld W, TicketJson Ticket)> AssignedAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        return (w, ticket);
    }

    // ---------------------------------------------------------------- messages

    [Fact]
    public async Task Customer_and_staff_chat_persists_in_order_and_customers_never_see_staff_identities()
    {
        var (w, t) = await AssignedAsync();

        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.SendMessageAsync(w.Customer, t.Id, "Hello, any news?"));
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.SendMessageAsync(w.Agent1, t.Id, "Looking into it."));
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.SendMessageAsync(w.Manager, t.Id, "Escalated."));

        var customerView = await w.MessagesAsync(w.Customer, t.Id, "?afterSequence=0");
        var staffView = await w.MessagesAsync(w.Manager, t.Id, "?afterSequence=0");
        Assert.Equal([1, 2, 3], customerView.Select(m => m.Sequence));
        Assert.Equal(["Hello, any news?", "Looking into it.", "Escalated."], staffView.Select(m => m.Content));
        Assert.Null(customerView[1].SenderUserId); // staff identity hidden from the customer
        Assert.Null(customerView[1].SenderDisplayName);
        Assert.True(customerView[1].SenderIsStaff);
        Assert.Equal(w.Customer.UserId, customerView[0].SenderUserId);
        Assert.Equal(w.Agent1.UserId, staffView[1].SenderUserId); // the server derived the sender from the token
        Assert.Equal(["Escalated.", "Looking into it.", "Hello, any news?"], (await w.MessagesAsync(w.Owner, t.Id)).Select(m => m.Content)); // default: newest first
        Assert.Equal([3], (await w.MessagesAsync(w.Owner, t.Id, "?afterSequence=2")).Select(m => m.Sequence)); // reconnect catch-up
    }

    [Fact]
    public async Task Messages_are_idempotent_validated_and_the_first_staff_reply_completes_the_response_sla()
    {
        var (w, t) = await AssignedAsync();
        var clientId = Guid.NewGuid();

        var first = await w.SendMessageAsync(w.Agent1, t.Id, "On it", clientId);
        var retry = await w.SendMessageAsync(w.Agent1, t.Id, "On it", clientId);

        await HttpAssert.StatusAsync(HttpStatusCode.Created, first);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, retry);
        Assert.Equal((await first.Content.ReadFromJsonAsync<MessageJson>())!.Id, (await retry.Content.ReadFromJsonAsync<MessageJson>())!.Id);
        Assert.Single(await w.MessagesAsync(w.Manager, t.Id));

        Assert.Equal(HttpStatusCode.BadRequest, (await w.SendMessageAsync(w.Agent1, t.Id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.SendMessageAsync(w.Agent1, t.Id, new string('x', 4001))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Agent1.PostAsync(w.Url($"tickets/{t.Id}/messages"), new { content = "no client id" })).StatusCode);

        var sla = await w.SlaAsync(t.Id);
        Assert.NotNull(sla.Current!.FirstResponseAtUtc);
        Assert.Equal("Met", sla.Current.ResponseState);
    }

    [Fact]
    public async Task A_client_supplied_sender_is_ignored_the_server_uses_the_authenticated_user()
    {
        var (w, t) = await AssignedAsync();

        var response = await w.Customer.PostAsync(w.Url($"tickets/{t.Id}/messages"),
            new { content = "spoof", clientMessageId = Guid.NewGuid(), senderUserId = w.Manager.UserId, senderIsStaff = true, tenantId = Guid.NewGuid() });

        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        var stored = (await w.MessagesAsync(w.Manager, t.Id)).Single();
        Assert.Equal(w.Customer.UserId, stored.SenderUserId);
        Assert.False(stored.SenderIsStaff);
    }

    [Fact]
    public async Task A_customer_message_does_not_count_as_the_first_response()
    {
        var (w, t) = await AssignedAsync();
        await w.SendMessageAsync(w.Customer, t.Id, "Please hurry");
        Assert.Null((await w.SlaAsync(t.Id)).Current!.FirstResponseAtUtc);
    }

    [Fact]
    public async Task Concurrent_messages_get_unique_gapless_sequences()
    {
        var (w, t) = await AssignedAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => w.SendMessageAsync(i % 2 == 0 ? w.Agent1 : w.Manager, t.Id, $"m{i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(Enumerable.Range(1, 8), (await w.MessagesAsync(w.Owner, t.Id, "?afterSequence=0")).Select(m => m.Sequence));
    }

    [Fact]
    public async Task Concurrent_retries_of_one_client_message_create_a_single_row()
    {
        var (w, t) = await AssignedAsync();
        var clientId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => w.SendMessageAsync(w.Agent1, t.Id, "same", clientId)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
        Assert.Single(await w.MessagesAsync(w.Owner, t.Id));
    }

    [Fact]
    public async Task Only_people_who_may_see_the_ticket_can_read_or_write_its_chat()
    {
        var (w, t) = await AssignedAsync();
        var otherTenant = await TicketWorld.CreateAsync(factory);
        var strangerCustomer = await ApiClient.AddMemberAsync(factory, w.Owner, w.Org.Id, "Customer"); // customer of the same tenant, not the requester

        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendMessageAsync(w.Agent2, t.Id, "not mine")).StatusCode); // unassigned agent
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent2.GetAsync(w.Url($"tickets/{t.Id}/messages"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendMessageAsync(strangerCustomer, t.Id, "not mine")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerCustomer.GetAsync(w.Url($"tickets/{t.Id}/messages"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.Owner.GetAsync(w.Url($"tickets/{t.Id}/messages"))).StatusCode); // no membership in this tenant
        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.Owner.PostAsync(w.Url($"tickets/{t.Id}/messages"), new { content = "x", clientMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(w.Url($"tickets/{t.Id}/messages"))).StatusCode);
        Assert.Empty(await w.MessagesAsync(w.Owner, t.Id));
    }

    [Fact]
    public async Task Access_follows_current_assignment_and_suspension()
    {
        var (w, t) = await AssignedAsync();
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.SendMessageAsync(w.Agent1, t.Id, "mine"));

        await w.AssignAsync(t.Id, w.Agent2Membership); // the last eligible assignee cannot be removed, so add agent2 first
        (await w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendMessageAsync(w.Agent1, t.Id, "after unassignment")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url($"tickets/{t.Id}/messages"))).StatusCode);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.SendMessageAsync(w.Agent2, t.Id, "taking over"));

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false, acknowledgeOpenWork = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendMessageAsync(w.Agent2, t.Id, "while suspended")).StatusCode);
    }

    [Fact]
    public async Task A_closed_ticket_rejects_new_messages()
    {
        var (w, t) = await AssignedAsync();
        await w.MoveAsync(w.Agent1, t.Id, "InProgress");
        await w.MoveAsync(w.Agent1, t.Id, "Resolved");
        await w.MoveAsync(w.Manager, t.Id, "Closed");
        Assert.Equal(HttpStatusCode.Conflict, (await w.SendMessageAsync(w.Customer, t.Id, "late")).StatusCode);
    }

    // ---------------------------------------------------------------- internal notes

    [Fact]
    public async Task Internal_notes_are_staff_only_and_never_reach_customers_in_any_representation()
    {
        var (w, t) = await AssignedAsync();
        const string secret = "SECRET-NOTE-CONTENT";

        var created = await w.AddNoteAsync(w.Agent1, t.Id, secret);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        await w.SendMessageAsync(w.Agent1, t.Id, "public reply");

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url($"tickets/{t.Id}/notes"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AddNoteAsync(w.Customer, t.Id, "I am sneaky")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent2.GetAsync(w.Url($"tickets/{t.Id}/notes"))).StatusCode); // unassigned agent
        Assert.Equal(secret, (await w.Manager.ReadAsync<PagedDto<NoteJson>>(await w.Manager.GetAsync(w.Url($"tickets/{t.Id}/notes")))).Items.Single().Content);

        // every customer-facing representation is free of the note
        string[] customerUrls = [$"tickets/{t.Id}", $"tickets/{t.Id}/activity", $"tickets/{t.Id}/messages", "notifications", $"tickets/{t.Id}/attachments"];
        foreach (var url in customerUrls)
        {
            var body = await (await w.Customer.GetAsync(w.Url(url))).Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain((await w.NotificationsAsync(w.Customer)), n => n.Type == "NoteAdded");
        Assert.Contains((await w.NotificationsAsync(w.Manager)), n => n.Type == "NoteAdded");
        Assert.DoesNotContain(secret, string.Join('|', (await w.NotificationsAsync(w.Manager)).Select(n => n.Subject + n.Detail)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notes_are_idempotent_immutable_history_with_server_author()
    {
        var (w, t) = await AssignedAsync();
        var clientId = Guid.NewGuid();

        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.AddNoteAsync(w.Agent1, t.Id, "first", clientId));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, await w.AddNoteAsync(w.Agent1, t.Id, "first", clientId));
        await w.AddNoteAsync(w.Manager, t.Id, "second");

        var notes = (await w.Owner.ReadAsync<PagedDto<NoteJson>>(await w.Owner.GetAsync(w.Url($"tickets/{t.Id}/notes")))).Items;
        Assert.Equal(["second", "first"], notes.Select(n => n.Content));
        Assert.Equal(w.Agent1.UserId, notes[1].AuthorUserId);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.AddNoteAsync(w.Manager, t.Id, "")).StatusCode);
        Assert.True((await w.Manager.PutAsync(w.Url($"tickets/{t.Id}/notes/{notes[0].Id}"), new { content = "edit" })).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed); // no edit/delete endpoint exists
    }

    // ---------------------------------------------------------------- notifications

    [Fact]
    public async Task Notifications_cover_the_ticket_lifecycle_with_unread_counts_and_read_transitions()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.Type == "TicketCreated" && n.TicketId == ticket.Id);
        Assert.Contains(await w.NotificationsAsync(w.Owner), n => n.Type == "TicketCreated");
        Assert.Empty(await w.NotificationsAsync(w.Customer)); // requester is not told about their own ticket

        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        Assert.Contains(await w.NotificationsAsync(w.Agent1), n => n.Type == "TicketAssigned");
        Assert.Contains(await w.NotificationsAsync(w.Agent2), n => n.Type == "TicketCreated" && n.TicketId == ticket.Id); // every active agent is told about a new ticket
        Assert.DoesNotContain(await w.NotificationsAsync(w.Agent2), n => n.Type == "TicketAssigned" && n.TicketId == ticket.Id); // but only the assignee about the assignment
        Assert.Contains(await w.NotificationsAsync(w.Customer), n => n.Type == "TicketStatusChanged" && n.Detail == "Assigned");

        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        Assert.Contains(await w.NotificationsAsync(w.Customer), n => n.Type == "TicketResolved");
        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.Type == "TicketResolved");

        await w.Customer.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { });
        Assert.Contains(await w.NotificationsAsync(w.Agent1), n => n.Type == "TicketReopened");

        await w.SendMessageAsync(w.Agent1, ticket.Id, "back on it");
        Assert.Contains(await w.NotificationsAsync(w.Customer), n => n.Type == "MessageReceived");
        await w.SendMessageAsync(w.Customer, ticket.Id, "thanks");
        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.Type == "MessageReceived");

        var unread = await w.UnreadAsync(w.Customer);
        var list = await w.NotificationsAsync(w.Customer);
        Assert.Equal(list.Count, unread);
        Assert.True(unread >= 3);
        Assert.Equal(list.OrderByDescending(n => n.CreatedAtUtc).Select(n => n.Id), list.Select(n => n.Id)); // newest first

        var one = list[0];
        Assert.Equal(HttpStatusCode.NoContent, (await w.Customer.PostAsync(w.Url($"notifications/{one.Id}/read"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Customer.PostAsync(w.Url($"notifications/{one.Id}/read"))).StatusCode); // idempotent
        Assert.Equal(unread - 1, await w.UnreadAsync(w.Customer));
        Assert.Equal(unread - 1, (await w.NotificationsAsync(w.Customer, "?unreadOnly=true")).Count);
        Assert.Single((await w.Customer.ReadAsync<PagedDto<NotificationJson>>(await w.Customer.GetAsync(w.Url("notifications?page=2&pageSize=" + (list.Count - 1))))).Items);

        Assert.Equal(HttpStatusCode.NotFound, (await w.Manager.PostAsync(w.Url($"notifications/{list[1].Id}/read"))).StatusCode); // somebody else's
        Assert.Equal(unread - 1, await w.UnreadAsync(w.Customer));

        Assert.Equal(HttpStatusCode.NoContent, (await w.Customer.PostAsync(w.Url("notifications/read-all"))).StatusCode);
        Assert.Equal(0, await w.UnreadAsync(w.Customer));
        Assert.True(await w.UnreadAsync(w.Manager) > 0); // read-all only touched the caller's rows
    }

    [Fact]
    public async Task Ticket_creation_notifies_all_other_active_staff_but_never_the_creator_or_a_customer()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var body = new { title = "Created by manager", description = "d", categoryId = w.CategoryId, customerId = w.CustomerContactId };
        var ticket = await w.CreateTicketAsync(w.Manager, body);

        foreach (var staff in new[] { w.Owner, w.Agent1, w.Agent2 })
        {
            Assert.Single(await w.NotificationsAsync(staff), n => n.Type == "TicketCreated" && n.TicketId == ticket.Id);
        }

        Assert.DoesNotContain(await w.NotificationsAsync(w.Manager), n => n.TicketId == ticket.Id); // the actor is excluded
        Assert.DoesNotContain(await w.NotificationsAsync(w.Customer), n => n.Type == "TicketCreated"); // even for their own contact
    }

    [Fact]
    public async Task The_global_notification_history_returns_only_the_callers_notifications_from_their_active_workspaces()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var other = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        var mine = await w.Agent1.ReadAsync<PagedDto<NotificationJson>>(await w.Agent1.GetAsync("/api/v1/notifications"));
        Assert.Contains(mine.Items, n => n.TenantId == w.Org.Id && n.TicketId == ticket.Id && n.Type == "TicketCreated");
        Assert.Equal((await w.NotificationsAsync(w.Agent1)).Count, mine.Items.Count); // same set as the workspace-scoped list
        var count = await w.Agent1.ReadAsync<UnreadJson>(await w.Agent1.GetAsync("/api/v1/notifications/unread-count"));
        Assert.Equal(mine.Items.Count(n => !n.IsRead), count.Count);

        // somebody from another workspace sees nothing of this one; nothing without a token
        var foreign = await other.Agent1.ReadAsync<PagedDto<NotificationJson>>(await other.Agent1.GetAsync("/api/v1/notifications"));
        Assert.DoesNotContain(foreign.Items, n => n.TenantId == w.Org.Id);

        (await w.Agent1.PostAsync("/api/v1/notifications/read-all")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await w.Agent1.ReadAsync<UnreadJson>(await w.Agent1.GetAsync("/api/v1/notifications/unread-count"))).Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/notifications")).StatusCode);
    }

    [Fact]
    public async Task Closing_a_ticket_notifies_the_requesting_customer_once_and_never_an_unrelated_customer()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        await w.MoveAsync(w.Manager, ticket.Id, "Closed");

        Assert.Single(await w.NotificationsAsync(w.Customer), n => n.Type == "TicketClosed" && n.TicketId == ticket.Id);
        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.Type == "TicketClosed" && n.TicketId == ticket.Id); // staff other than the actor

        // A repeated close is rejected and must not add a second notification.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "Closed" })).StatusCode);
        Assert.Single(await w.NotificationsAsync(w.Customer), n => n.Type == "TicketClosed" && n.TicketId == ticket.Id);

        // Tickets of other requesters (or without any customer) never notify this customer.
        var internalTicket = await w.CreateTicketAsync(w.Agent1);
        await w.MoveAsync(w.Agent1, internalTicket.Id, "InProgress");
        await w.MoveAsync(w.Agent1, internalTicket.Id, "Resolved");
        await w.MoveAsync(w.Manager, internalTicket.Id, "Closed");
        Assert.DoesNotContain(await w.NotificationsAsync(w.Customer), n => n.TicketId == internalTicket.Id);
    }

    [Fact]
    public async Task Notifications_never_cross_tenants_and_are_unique_per_event()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        await a.CreateTicketAsync(a.Customer);

        Assert.Empty(await b.NotificationsAsync(b.Owner));
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url("notifications"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.PostAsync(a.Url("notifications/read-all"))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var duplicates = await db.Notifications.IgnoreQueryFilters().GroupBy(n => new { n.TenantId, n.UserId, n.DedupKey }).CountAsync(g => g.Count() > 1);
        Assert.Equal(0, duplicates);
    }

    // ---------------------------------------------------------------- attachments

    [Fact]
    public async Task Attachments_upload_list_and_download_with_safe_headers_and_no_physical_path()
    {
        var (w, t) = await AssignedAsync();

        var upload = await w.UploadAsync(w.Customer, t.Id, "..\\..\\evil/screenshot.png", ConversationExtensions.Png);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, upload);
        var meta = (await upload.Content.ReadFromJsonAsync<AttachmentJson>())!;
        Assert.Equal("screenshot.png", meta.FileName); // directories stripped
        Assert.Equal("image/png", meta.ContentType); // decided by the server
        Assert.Null(meta.UploadedByUserId); // customer view never exposes uploader identities
        Assert.DoesNotContain("StorageKey", await upload.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        foreach (var who in new[] { w.Customer, w.Agent1, w.Manager })
        {
            var download = await who.GetAsync(w.Url($"tickets/{t.Id}/attachments/{meta.Id}"));
            await HttpAssert.StatusAsync(HttpStatusCode.OK, download);
            Assert.Equal(ConversationExtensions.Png, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        }

        Assert.Single(await w.Manager.ReadAsync<List<AttachmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{t.Id}/attachments"))));
        Assert.Contains(await w.ActivityAsync(w.Manager, t.Id), a => a.Type == "AttachmentAdded");
        Assert.DoesNotContain(await w.ActivityAsync(w.Customer, t.Id), a => a.Type == "AttachmentAdded");
    }

    [Fact]
    public async Task Attachments_reject_executables_disguised_files_and_oversized_content()
    {
        var (w, t) = await AssignedAsync();
        byte[] exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00];

        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "setup.exe", exe)).StatusCode); // extension not allowed
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "photo.png", exe)).StatusCode); // content does not match
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "photo.png.exe", ConversationExtensions.Png)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "noext", ConversationExtensions.Png)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "notes.txt", [0x68, 0x00, 0x69])).StatusCode); // NUL byte: binary, not text
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "empty.png", [])).StatusCode);
        var tooBig = new byte[(10 * 1024 * 1024) + 1];
        ConversationExtensions.Png.CopyTo(tooBig, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.UploadAsync(w.Customer, t.Id, "huge.png", tooBig)).StatusCode);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await w.UploadAsync(w.Customer, t.Id, "notes.txt", "hello ünïcode"u8.ToArray()));
        Assert.Single(await w.Manager.ReadAsync<List<AttachmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{t.Id}/attachments"))));
    }

    [Fact]
    public async Task Internal_attachments_are_invisible_to_customers_and_access_is_checked_on_download()
    {
        var (w, t) = await AssignedAsync();
        var other = await TicketWorld.CreateAsync(factory);
        var otherTicket = await other.CreateTicketAsync(other.Customer);

        Assert.Equal(HttpStatusCode.Forbidden, (await w.UploadAsync(w.Customer, t.Id, "x.png", ConversationExtensions.Png, internalOnly: true)).StatusCode);
        var internalFile = (await (await w.UploadAsync(w.Agent1, t.Id, "internal.png", ConversationExtensions.Png, internalOnly: true)).Content.ReadFromJsonAsync<AttachmentJson>())!;

        Assert.Empty(await w.Customer.ReadAsync<List<AttachmentJson>>(await w.Customer.GetAsync(w.Url($"tickets/{t.Id}/attachments"))));
        Assert.Equal(HttpStatusCode.NotFound, (await w.Customer.GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent2.GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode); // unassigned agent: the ticket is visible, its files are not
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent2.GetAsync(w.Url($"tickets/{t.Id}/attachments"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Manager.GetAsync(w.Url($"tickets/{otherTicket.Id}/attachments/{internalFile.Id}"))).StatusCode); // wrong ticket id
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode); // other tenant
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode);

        // revoked access: the agent is unassigned, so the very same download link stops working
        await w.AssignAsync(t.Id, w.Agent2Membership);
        (await w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url($"tickets/{t.Id}/attachments/{internalFile.Id}"))).StatusCode);
    }

    private static async Task<int> UnreadTotalAsync(TicketWorld w, ApiClient who)
    {
        var response = await who.PostAsync(w.Url("graphql"), new { query = "{ chatUnreadSummary { total } }" });
        response.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("chatUnreadSummary").GetProperty("total").GetInt32();
    }

    private static Task<HttpResponseMessage> AcknowledgeAsync(TicketWorld w, ApiClient who, Guid ticketId, int? delivered, int? read) =>
        who.PostAsync(w.Url($"tickets/{ticketId}/messages/cursor"), new { deliveredSequence = delivered, readSequence = read });

    [Fact]
    public async Task Unread_counts_follow_each_users_read_cursor_and_respect_chat_access()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        await w.SendMessageAsync(w.Agent1, ticket.Id, "first reply");
        var last = await w.Agent1.ReadAsync<MessageJson>(await w.SendMessageAsync(w.Agent1, ticket.Id, "second reply"));
        await w.SendMessageAsync(w.Customer, ticket.Id, "customer answer");
        var note = await w.Agent1.ReadAsync<NoteJson>(await w.AddNoteAsync(w.Agent1, ticket.Id, "private note"));

        // Own messages and internal notes never count; an unassigned Agent has no conversation access and therefore no unread data.
        Assert.Equal(2, await UnreadTotalAsync(w, w.Customer));
        Assert.Equal(1, await UnreadTotalAsync(w, w.Agent1));
        Assert.Equal(3, await UnreadTotalAsync(w, w.Manager));
        Assert.Equal(0, await UnreadTotalAsync(w, w.Agent2));
        Assert.Equal(HttpStatusCode.Forbidden, (await AcknowledgeAsync(w, w.Agent2, ticket.Id, null, last.Sequence)).StatusCode);

        // Only sequences of stored chat messages can be acknowledged: not a note's sequence, not a future one, not nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await AcknowledgeAsync(w, w.Customer, ticket.Id, null, note.Sequence)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcknowledgeAsync(w, w.Customer, ticket.Id, null, note.Sequence + 100)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcknowledgeAsync(w, w.Customer, ticket.Id, null, null)).StatusCode);

        var read = await w.Customer.ReadAsync<CursorJson>(await AcknowledgeAsync(w, w.Customer, ticket.Id, null, last.Sequence));
        Assert.Equal((last.Sequence, last.Sequence), (read.DeliveredSequence, read.ReadSequence)); // reading implies delivery
        Assert.Equal(0, await UnreadTotalAsync(w, w.Customer));
        Assert.Equal(3, await UnreadTotalAsync(w, w.Manager)); // another user's cursor is independent

        // A late or repeated acknowledgement never moves the cursor backwards.
        var stale = await w.Customer.ReadAsync<CursorJson>(await AcknowledgeAsync(w, w.Customer, ticket.Id, last.Sequence - 1, last.Sequence - 1));
        Assert.Equal((last.Sequence, last.Sequence), (stale.DeliveredSequence, stale.ReadSequence));

        // Another workspace never sees or moves this cursor.
        var other = await TicketWorld.CreateAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await AcknowledgeAsync(w, other.Owner, ticket.Id, null, last.Sequence)).StatusCode);
    }

    private sealed record CursorJson(Guid TicketId, int DeliveredSequence, int ReadSequence);
}
