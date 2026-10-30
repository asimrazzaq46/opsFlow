using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Outbox;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Transactional outbox: atomic with the business change, recoverable, retried per channel, dead-lettered when exhausted.</summary>
/// <remarks>One factory (database) per test: assertions about "nothing is due" must not see other tests' events.</remarks>
public sealed class OutboxTests : IDisposable
{
    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    private static object NewTicket(TicketWorld w, params Guid[] assignees) =>
        new { title = "Outbox me", description = "d", categoryId = w.CategoryId, priority = "Urgent", assigneeMembershipIds = assignees };

    [Fact]
    public async Task A_committed_business_change_leaves_a_durable_event_and_the_dispatcher_completes_it()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        var pending = await factory.OutboxAsync(w.Org.Id, OutboxEvents.NotificationCreated);
        Assert.NotEmpty(pending);
        Assert.All(pending, m => Assert.Equal(OutboxStatus.Pending, m.Status)); // nothing was pushed in the request itself
        Assert.All(pending, m => Assert.Equal(ticket.Id, m.AggregateId));
        Assert.DoesNotContain(pending, m => m.Payload.Contains("Printer", StringComparison.Ordinal)); // ids only, never content

        await factory.DrainOutboxAsync();

        Assert.All(await factory.OutboxAsync(w.Org.Id), m => Assert.Equal(OutboxStatus.Completed, m.Status));
        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.Type == "TicketCreated"); // the notification row was committed with the ticket
    }

    [Fact]
    public async Task A_failed_business_transaction_leaves_no_event_behind()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var before = (await factory.OutboxAsync(w.Org.Id)).Count;

        factory.Faults.FailNextSave();
        var response = await w.TryCreateTicketAsync(w.Manager, NewTicket(w));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, (await factory.OutboxAsync(w.Org.Id)).Count);
    }

    [Fact]
    public async Task An_event_claimed_by_a_crashed_worker_is_recovered_after_its_lease_expires_and_a_live_lease_is_respected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Customer);
        var ids = (await factory.OutboxAsync(w.Org.Id)).Select(m => m.Id).ToList();
        var now = factory.Clock.GetUtcNow().UtcDateTime;

        // "worker crashed after the claim": status Claimed with a lease that is still valid -> nobody else may take it
        await factory.WithDbAsync(db => db.OutboxMessages.Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.Claimed).SetProperty(m => m.ClaimedUntilUtc, (DateTime?)now.AddMinutes(5)).SetProperty(m => m.ClaimToken, (Guid?)Guid.NewGuid())));
        Assert.Equal(0, await factory.DrainOutboxAsync());

        factory.Clock.Advance(TimeSpan.FromMinutes(6)); // the lease expired
        Assert.Equal(ids.Count, await factory.DrainOutboxAsync());
        Assert.All(await factory.OutboxAsync(w.Org.Id), m => Assert.Equal(OutboxStatus.Completed, m.Status));
    }

    [PostgresOnlyFact]
    public async Task Concurrent_dispatchers_claim_every_event_exactly_once()
    {
        var w = await TicketWorld.CreateAsync(factory);
        for (var i = 0; i < 5; i++)
        {
            await w.CreateTicketAsync(w.Customer);
        }

        var processed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(factory.DrainOutboxAsync)));

        var rows = await factory.OutboxAsync(w.Org.Id);
        Assert.Equal(rows.Count, processed.Sum()); // nobody claimed an event another worker already held
        Assert.All(rows, m => Assert.Equal(1, m.AttemptCount));
        Assert.All(rows, m => Assert.Equal(OutboxStatus.Completed, m.Status));
    }

    [Fact]
    public async Task Smtp_outage_does_not_roll_back_the_business_change_and_only_the_email_channel_is_retried()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, NewTicket(w));
        await factory.DrainOutboxAsync();
        var emailsBefore = factory.Emails.Count(w.Agent1.Email);

        factory.Emails.Unavailable = true;
        var assigned = await w.AssignAsync(ticket.Id, w.Agent1Membership); // 200: the assignment does not depend on SMTP
        Assert.Equal("Assigned", assigned.Status);
        await factory.DrainOutboxAsync();

        var failing = (await factory.OutboxAsync(w.Org.Id, OutboxEvents.NotificationCreated)).Single(m => m.Status == OutboxStatus.RetryScheduled);
        Assert.True(failing.CompletedChannels.HasFlag(OutboxChannels.Realtime)); // the finished channel is remembered ...
        Assert.False(failing.CompletedChannels.HasFlag(OutboxChannels.Email)); // ... only the failed one repeats
        Assert.NotNull(failing.LastError);
        Assert.Equal(emailsBefore, factory.Emails.Count(w.Agent1.Email));

        factory.Emails.Unavailable = false;
        Assert.Equal(0, await factory.DrainOutboxAsync()); // not due yet: backoff is respected
        factory.Clock.Advance(TimeSpan.FromHours(1));
        await factory.DrainOutboxAsync();
        await factory.DrainOutboxAsync(); // a second pass must not send again

        Assert.Equal(emailsBefore + 1, factory.Emails.Count(w.Agent1.Email));
        Assert.All(await factory.OutboxAsync(w.Org.Id), m => Assert.Equal(OutboxStatus.Completed, m.Status));
        Assert.Contains("assigned", factory.Emails.Last(w.Agent1.Email, "Open OpsFlow").Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_exhausted_event_becomes_a_visible_dead_letter_and_is_not_retried_forever()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, NewTicket(w));
        factory.Emails.Unavailable = true;
        await w.AssignAsync(ticket.Id, w.Agent1Membership);

        for (var i = 0; i < 10; i++)
        {
            await factory.DrainOutboxAsync();
            factory.Clock.Advance(TimeSpan.FromHours(1));
        }

        var dead = (await factory.OutboxAsync(w.Org.Id)).Single(m => m.Status == OutboxStatus.DeadLetter);
        Assert.Equal(8, dead.AttemptCount); // Outbox:MaxAttempts
        Assert.Contains("IOException", dead.LastError, StringComparison.Ordinal);
        Assert.Equal(0, await factory.DrainOutboxAsync());
        factory.Emails.Unavailable = false;
    }

    [Fact]
    public async Task Email_recipients_are_revalidated_at_delivery_time()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, NewTicket(w));
        await factory.DrainOutboxAsync();
        var before = factory.Emails.Count(w.Agent1.Email);

        await w.AssignAsync(ticket.Id, w.Agent1Membership); // e-mail event is queued ...
        await factory.SuspendMembershipAsync(w.Agent1Membership); // ... then the agent loses access
        await factory.DrainOutboxAsync();

        Assert.Equal(before, factory.Emails.Count(w.Agent1.Email)); // nothing about the ticket is disclosed
        Assert.All(await factory.OutboxAsync(w.Org.Id), m => Assert.Equal(OutboxStatus.Completed, m.Status)); // skipped, not stuck
    }

    [Fact]
    public async Task The_same_business_event_cannot_be_enqueued_twice()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using var scope = factory.Services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().Begin(w.Org.Id);
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        outbox.Stage(w.Org.Id, "test.event", null, new { }, "dup-key", OutboxChannels.Cache);
        await db.SaveChangesAsync();
        outbox.Stage(w.Org.Id, "test.event", null, new { }, "dup-key", OutboxChannels.Cache);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup));
    }

    [Fact]
    public async Task An_unsupported_event_goes_straight_to_the_dead_letters()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().Begin(w.Org.Id);
            scope.ServiceProvider.GetRequiredService<OutboxWriter>().Stage(w.Org.Id, "unknown.event", null, new { }, $"x-{Guid.NewGuid()}", OutboxChannels.Realtime);
            await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().SaveChangesAsync();
        }

        await factory.DrainOutboxAsync();

        var row = (await factory.OutboxAsync(w.Org.Id, "unknown.event")).Single();
        Assert.Equal(OutboxStatus.DeadLetter, row.Status);
        Assert.Equal(1, row.AttemptCount);
    }

    [Fact]
    public async Task Private_notes_and_other_tenants_never_reach_the_customer_or_foreign_users()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var other = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        await w.AddNoteAsync(w.Agent1, ticket.Id, "internal only");
        await using var customerHub = HubClient.Create(factory, w.Customer.AccessToken);
        await using var foreignHub = HubClient.Create(factory, other.Manager.AccessToken);
        await customerHub.StartAsync();
        await foreignHub.StartAsync();

        await w.SendMessageAsync(w.Agent1, ticket.Id, "public reply");
        Assert.True(await customerHub.WaitForAsync(e => e.Name == "ticket.message.created"));
        await factory.DrainOutboxAsync();

        Assert.DoesNotContain(customerHub.Events, e => e.Name == "ticket.note.created");
        Assert.Empty(foreignHub.Events); // another tenant's manager receives nothing
        Assert.Empty(await other.NotificationsAsync(other.Manager));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync(other.Url($"tickets/{ticket.Id}"))).StatusCode);
    }

    [Fact]
    public async Task A_slow_delivery_never_leaves_later_events_of_the_batch_with_an_already_expired_lease()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Manager, NewTicket(w, w.Agent1Membership)); // two "assigned" e-mail events
        await w.CreateTicketAsync(w.Manager, NewTicket(w, w.Agent1Membership));
        var sends = 0;
        DateTime? leaseDuringSecondSend = null;
        DateTime? clockDuringSecondSend = null;
        factory.Emails.OnSend = to =>
        {
            if (!string.Equals(to, w.Agent1.Email, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (++sends == 1)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(5)); // the first SMTP send "takes" longer than the whole lease
                return;
            }

            clockDuringSecondSend = factory.Clock.GetUtcNow().UtcDateTime;
            leaseDuringSecondSend = factory.WithDbAsync(db => db.OutboxMessages.Where(m => m.TenantId == w.Org.Id && m.Status == OutboxStatus.Claimed).Select(m => m.ClaimedUntilUtc).SingleAsync()).GetAwaiter().GetResult();
        };

        await factory.DrainOutboxAsync();

        Assert.Equal(2, sends);
        Assert.NotNull(leaseDuringSecondSend);
        Assert.True(leaseDuringSecondSend > clockDuringSecondSend, "The second event must hold a lease measured from the current clock (fresh claim time / renewal), not from the batch start.");
    }

    [PostgresOnlyFact]
    public async Task Two_workers_do_not_deliver_the_same_event_while_a_slow_send_holds_a_valid_lease()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Manager, NewTicket(w, w.Agent1Membership));
        var before = factory.Emails.Count(w.Agent1.Email);
        factory.Emails.Delay = TimeSpan.FromSeconds(2);

        await Task.WhenAll(factory.DrainOutboxAsync(), factory.DrainOutboxAsync());

        Assert.Equal(before + 1, factory.Emails.Count(w.Agent1.Email));
        Assert.All(await factory.OutboxAsync(w.Org.Id), m => Assert.Equal(OutboxStatus.Completed, m.Status));
    }
}
