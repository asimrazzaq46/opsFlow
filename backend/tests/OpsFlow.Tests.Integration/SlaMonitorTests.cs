using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Domain.Notifications;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// Automated SLA monitoring on controlled time. Urgent policy: first response 60 min, resolution 480 min; warning at 75 % (45 / 360 min).
/// </summary>
public class SlaMonitorTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(TicketWorld World, TicketJson Ticket)> UrgentTicketAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Sla me", description = "d", categoryId = w.CategoryId, priority = "Urgent", assigneeMembershipIds = new[] { w.Agent1Membership } });
        return (w, ticket);
    }

    [Fact]
    public async Task First_response_warns_once_inside_the_window_and_a_timely_reply_satisfies_it()
    {
        var (w, ticket) = await UrgentTicketAsync();

        await w.AdvanceAsync(TimeSpan.FromMinutes(44));
        await factory.ScanSlaAsync();
        Assert.Null((await factory.CyclesAsync(ticket.Id)).Single().ResponseWarnedAtUtc); // 73 % elapsed: too early

        await w.AdvanceAsync(TimeSpan.FromMinutes(2));
        await factory.ScanSlaAsync();
        Assert.NotNull((await factory.CyclesAsync(ticket.Id)).Single().ResponseWarnedAtUtc);
        await factory.ScanSlaAsync(); // duplicate scan: no duplicate warning
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaWarning, "FirstResponse"));
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Agent1.UserId, NotificationType.SlaWarning, "FirstResponse"));
        Assert.Equal(0, await factory.SlaNotificationsAsync(w.Customer.UserId, NotificationType.SlaWarning, "FirstResponse")); // customers never see SLA data
        Assert.Equal(0, await factory.SlaNotificationsAsync(w.Agent2.UserId, NotificationType.SlaWarning, "FirstResponse")); // not on the ticket

        await w.SendMessageAsync(w.Agent1, ticket.Id, "on it");
        await w.AdvanceAsync(TimeSpan.FromMinutes(30));
        await factory.ScanSlaAsync();

        Assert.Equal("Met", (await w.SlaAsync(ticket.Id)).Current!.ResponseState);
        Assert.Null((await factory.CyclesAsync(ticket.Id)).Single().ResponseBreachedAtUtc);
    }

    [Fact]
    public async Task A_missing_first_response_is_a_breach_and_a_late_reply_does_not_erase_it()
    {
        var (w, ticket) = await UrgentTicketAsync();

        await w.AdvanceAsync(TimeSpan.FromMinutes(61));
        await factory.ScanSlaAsync();
        var breached = (await factory.CyclesAsync(ticket.Id)).Single();
        Assert.NotNull(breached.ResponseBreachedAtUtc);
        Assert.Null(breached.ResponseWarnedAtUtc); // never a late "warning" once the deadline passed
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaBreached, "FirstResponse"));

        await w.SendMessageAsync(w.Agent1, ticket.Id, "sorry for the delay");
        await factory.ScanSlaAsync();

        var cycle = (await factory.CyclesAsync(ticket.Id)).Single();
        Assert.Equal(breached.ResponseBreachedAtUtc, cycle.ResponseBreachedAtUtc); // recorded once, never rewritten
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaBreached, "FirstResponse"));
        Assert.Equal("Missed", (await w.SlaAsync(ticket.Id)).Current!.ResponseState);
    }

    [Fact]
    public async Task A_note_or_a_customer_message_does_not_satisfy_the_first_response()
    {
        var (w, ticket) = await UrgentTicketAsync();
        await w.AddNoteAsync(w.Agent1, ticket.Id, "internal thought");
        await w.SendMessageAsync(w.Customer, ticket.Id, "hello?");

        await w.AdvanceAsync(TimeSpan.FromMinutes(61));

        await factory.ScanSlaAsync();
        var cycle = (await factory.CyclesAsync(ticket.Id)).Single();
        Assert.Null(cycle.FirstResponseAtUtc);
        Assert.NotNull(cycle.ResponseBreachedAtUtc);
    }

    [Fact]
    public async Task Resolution_warns_at_the_threshold_and_breaches_after_the_deadline_exactly_once()
    {
        var (w, ticket) = await UrgentTicketAsync();
        await w.SendMessageAsync(w.Agent1, ticket.Id, "working");

        await w.AdvanceAsync(TimeSpan.FromMinutes(361));
        await factory.ScanSlaAsync();
        Assert.NotNull((await factory.CyclesAsync(ticket.Id)).Single().ResolutionWarnedAtUtc);
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaWarning, "Resolution"));

        await w.AdvanceAsync(TimeSpan.FromMinutes(120));
        await factory.ScanSlaAsync();
        await Task.WhenAll(factory.ScanSlaAsync(), factory.ScanSlaAsync()); // duplicate workers (SQLite may serialize them)
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaBreached, "Resolution"));
        Assert.Equal(1, await factory.WithDbAsync(db => db.TicketSlaCycles.IgnoreQueryFilters().CountAsync(c => c.TicketId == ticket.Id && c.ResolutionBreachedAtUtc != null)));
    }

    [Fact]
    public async Task A_ticket_resolved_in_time_never_gets_a_stale_warning_or_breach()
    {
        var (w, ticket) = await UrgentTicketAsync();
        await w.SendMessageAsync(w.Agent1, ticket.Id, "fixing");
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");

        await w.AdvanceAsync(TimeSpan.FromMinutes(600));
        await factory.ScanSlaAsync();

        var cycle = (await factory.CyclesAsync(ticket.Id)).Single();
        Assert.Null(cycle.ResponseBreachedAtUtc);
        Assert.Null(cycle.ResolutionWarnedAtUtc);
        Assert.Null(cycle.ResolutionBreachedAtUtc);
    }

    [Fact]
    public async Task A_reopened_ticket_is_monitored_in_its_new_cycle_and_the_old_cycle_stays_untouched()
    {
        var (w, ticket) = await UrgentTicketAsync();
        await w.SendMessageAsync(w.Agent1, ticket.Id, "done soon");
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        await w.AdvanceAsync(TimeSpan.FromMinutes(20));
        (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { })).EnsureSuccessStatusCode();

        await w.AdvanceAsync(TimeSpan.FromMinutes(361)); // 75 % of the NEW cycle (the old one is long finished)
        await factory.ScanSlaAsync();
        await w.AdvanceAsync(TimeSpan.FromMinutes(130));
        await factory.ScanSlaAsync();

        var cycles = await factory.CyclesAsync(ticket.Id);
        Assert.Equal(2, cycles.Count);
        Assert.Null(cycles[0].ResolutionWarnedAtUtc); // history untouched
        Assert.Null(cycles[0].ResolutionBreachedAtUtc);
        Assert.NotNull(cycles[1].ResolutionWarnedAtUtc);
        Assert.NotNull(cycles[1].ResolutionBreachedAtUtc);
        Assert.Equal(cycles[1].Id, (await w.SlaAsync(ticket.Id)).Current!.Id);
        await factory.ScanSlaAsync(); // repeated runs add nothing
        Assert.Equal(cycles[1].ResolutionBreachedAtUtc, (await factory.CyclesAsync(ticket.Id))[1].ResolutionBreachedAtUtc);
        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaBreached, "Resolution"));
    }

    [Fact]
    public async Task Sla_notifications_reach_only_current_eligible_staff_of_the_tenant()
    {
        var (w, ticket) = await UrgentTicketAsync();
        await factory.SuspendMembershipAsync(w.Agent1Membership); // removed agent
        await w.AdvanceAsync(TimeSpan.FromMinutes(61));
        var other = await TicketWorld.CreateAsync(factory); // created after the clock moved: its tokens are still valid

        await factory.ScanSlaAsync();

        Assert.Equal(1, await factory.SlaNotificationsAsync(w.Manager.UserId, NotificationType.SlaBreached, "FirstResponse"));
        Assert.Equal(0, await factory.SlaNotificationsAsync(w.Agent1.UserId, NotificationType.SlaBreached, "FirstResponse"));
        Assert.DoesNotContain(await other.NotificationsAsync(other.Manager), n => n.Type.StartsWith("Sla", StringComparison.Ordinal));
        Assert.Contains(await w.NotificationsAsync(w.Manager), n => n.TicketId == ticket.Id && n.Type == "SlaBreached"); // visible in the normal notification center
    }

}

/// <summary>Own host so its log output can be inspected.</summary>
public sealed class SlaDiagnosticsTests : IDisposable
{
    private readonly LoggingApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_breach_without_any_eligible_recipient_is_recorded_without_notifications_and_logged()
    {
        var w = await TicketWorld.CreateAsync(_factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Nobody home", description = "d", categoryId = w.CategoryId, priority = "Urgent", assigneeMembershipIds = new[] { w.Agent1Membership } });
        foreach (var membership in new[] { w.OwnerMembership, w.ManagerMembership, w.Agent1Membership })
        {
            await _factory.SuspendMembershipAsync(membership);
        }

        await w.AdvanceAsync(TimeSpan.FromMinutes(61));
        await _factory.ScanSlaAsync();

        Assert.NotNull((await _factory.CyclesAsync(ticket.Id)).Single().ResponseBreachedAtUtc); // the authoritative record is kept
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Notifications.IgnoreQueryFilters().CountAsync(n => n.TicketId == ticket.Id && n.Type == NotificationType.SlaBreached)));
        var log = Assert.Single(_factory.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("no eligible recipient", StringComparison.Ordinal));
        Assert.Contains(w.Org.Id.ToString(), log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nobody home", log.Message, StringComparison.Ordinal); // no ticket content in the diagnostic
    }

    [Fact]
    public void A_disabled_worker_is_announced_as_intentional_in_tests_and_never_as_the_production_warning()
    {
        _ = _factory.CreateClient();

        Assert.Contains(_factory.Logs, l => l.Level == LogLevel.Information && l.Message.Contains("intentionally disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs, l => l.Message.Contains("BACKGROUND WORKER IS DISABLED", StringComparison.Ordinal));
    }
}
