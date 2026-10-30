using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tickets;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Tests.Integration.Support;

/// <summary>What the seeder created, counted while creating (independent of the code under test).</summary>
public sealed class SeedTotals
{
    public int Tickets { get; set; }
    public int Open { get; set; }
    public int OpenUnassigned { get; set; }
    public int OpenOverdue { get; set; }
    public int CompletedCycles { get; set; }
    public int CompletedCyclesMet { get; set; }
    public int ReopenedCycles { get; set; }
    public int Assignments { get; set; }
    public int OpenAssignments { get; set; } // assignments on tickets that are not finished (one ticket with two agents counts twice)
    public int Customers { get; set; }
    public double CompletedMinutesSum { get; set; }
    public List<double> CompletedMinutes { get; } = [];
}

/// <summary>Bulk, deterministic tenant data straight through the domain factories (no HTTP) for query-count and scale tests.</summary>
public static class BulkSeed
{
    public static async Task<SeedTotals> AddTicketsAsync(TicketWorld w, int count, int firstNumber, int customerCount = 20)
    {
        var totals = new SeedTotals();
        var now = w.Factory.Clock.GetUtcNow().UtcDateTime;
        var tenantId = w.Org.Id;

        Dictionary<TicketPriority, SlaPolicy> policies;
        List<Guid> customers;
        using (var scope = Open(w, out var db))
        {
            policies = await db.SlaPolicies.AsNoTracking().Where(p => p.IsActive).ToDictionaryAsync(p => p.Priority);
            customers = await db.Customers.AsNoTracking().Select(c => c.Id).ToListAsync();
            while (customers.Count < customerCount)
            {
                var c = Customer.Create(tenantId, $"Bulk Customer {customers.Count}", $"bulk-{Guid.NewGuid():N}@example.com", null, now);
                db.Customers.Add(c);
                customers.Add(c.Id);
                totals.Customers++;
            }

            await db.SaveChangesAsync();
            await db.TicketCounters.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastNumber, c => Math.Max(c.LastNumber, firstNumber + count)));
        }

        for (var batchStart = 0; batchStart < count; batchStart += 1000)
        {
            using var scope = Open(w, out var db);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = batchStart; i < Math.Min(count, batchStart + 1000); i++)
            {
                AddTicket(w, db, totals, policies, customers, i, firstNumber + i, now);
            }

            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }

        using (var scope = Open(w, out var db)) // first responses for 60% of the first cycles that were started (30 minutes after start)
        {
            await db.TicketSlaCycles.Where(c => c.CycleNumber == 1 && c.Reason == SlaCycleReason.Initial && c.StartedAtUtc < now.AddMinutes(-60) && c.FirstResponseAtUtc == null)
                .Where(c => db.Tickets.Any(t => t.Id == c.TicketId && t.Number % 5 != 0))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FirstResponseAtUtc, c => (DateTime?)c.StartedAtUtc.AddMinutes(30)));
        }

        return totals;
    }

    /// <summary>One ticket created at an exact instant, optionally resolved after N minutes (assigned to Agent1), for boundary/median tests.</summary>
    public static async Task AddSimpleTicketAsync(TicketWorld w, DateTime createdUtc, int? resolvedAfterMinutes = null)
    {
        using var scope = Open(w, out var db);
        var policy = await db.SlaPolicies.AsNoTracking().FirstAsync(p => p.IsActive && p.Priority == TicketPriority.Medium);
        var number = (await db.TicketCounters.Select(c => c.LastNumber).SingleAsync()) + 1;
        await db.TicketCounters.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastNumber, number));
        var ticket = Ticket.Create(w.Org.Id, number, "Boundary ticket", "x", TicketPriority.Medium, w.CategoryId, null, w.Manager.UserId, createdUtc);
        db.Tickets.Add(ticket);
        db.TicketActivities.Add(ticket.Record(TicketActivityType.Created, w.Manager.UserId, createdUtc, newValue: "Open"));
        var cycle = TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), SlaCycleReason.Initial, policy, createdUtc);
        db.TicketSlaCycles.Add(cycle);
        if (resolvedAfterMinutes is { } minutes)
        {
            var at = createdUtc.AddMinutes(minutes);
            db.TicketAssignments.Add(TicketAssignment.Create(w.Org.Id, ticket.Id, w.Agent1Membership, w.ManagerMembership, createdUtc));
            cycle.Resolve(at);
            ticket.MarkResolved(at);
            db.TicketActivities.Add(ticket.Record(TicketActivityType.Resolved, w.Manager.UserId, at));
        }

        await db.SaveChangesAsync();
    }

    private static IServiceScope Open(TicketWorld w, out OpsFlowDbContext db)
    {
        var scope = w.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(w.Org.Id, w.ManagerMembership, OpsFlow.Domain.Tenants.TenantRole.Manager);
        db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return scope;
    }

    private static void AddTicket(TicketWorld w, OpsFlowDbContext db, SeedTotals totals, Dictionary<TicketPriority, SlaPolicy> policies, List<Guid> customers, int i, long number, DateTime now)
    {
        var priority = (TicketPriority)(i % 4);
        var created = now.AddDays(-(i % 60)).AddMinutes(-(i % 1440) - 5);
        var policy = policies[priority];
        var ticket = Ticket.Create(w.Org.Id, number, $"Bulk ticket {number}", "Generated for scale testing", priority, w.CategoryId, customers[i % customers.Count], w.Manager.UserId, created);
        db.Tickets.Add(ticket);
        db.TicketActivities.Add(ticket.Record(TicketActivityType.Created, w.Manager.UserId, created, newValue: "Open"));
        var cycle = TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), SlaCycleReason.Initial, policy, created);
        db.TicketSlaCycles.Add(cycle);

        var kind = i % 20;
        var agents = kind is 10 or 11 ? new[] { w.Agent1Membership, w.Agent2Membership } : kind is >= 4 and <= 9 or 17 or 19 ? new[] { i % 2 == 0 ? w.Agent1Membership : w.Agent2Membership } : [];
        foreach (var membership in agents)
        {
            db.TicketAssignments.Add(TicketAssignment.Create(w.Org.Id, ticket.Id, membership, w.ManagerMembership, created));
            totals.Assignments++;
        }

        totals.Tickets++;
        var finished = false;
        if (kind is >= 12 and <= 16 or 17 or 18)
        {
            var minutes = Math.Min(30 + ((i * 37) % 3000), Math.Max(1, (int)(now - created).TotalMinutes - 1)); // never resolved in the future
            var resolvedAt = created.AddMinutes(minutes);
            cycle.Resolve(resolvedAt);
            ticket.MarkResolved(resolvedAt);
            db.TicketActivities.Add(ticket.Record(TicketActivityType.Resolved, w.Manager.UserId, resolvedAt));
            totals.CompletedCycles++;
            totals.CompletedMinutesSum += minutes;
            totals.CompletedMinutes.Add(minutes);
            if (resolvedAt <= cycle.ResolutionDueAtUtc)
            {
                totals.CompletedCyclesMet++;
            }

            finished = true;
            if (kind == 18)
            {
                ticket.SetStatus(TicketStatus.Closed, resolvedAt);
            }
            else if (kind == 17) // reopened: a NEW cycle while the first one stays historical
            {
                var reopenedAt = resolvedAt.AddMinutes(10);
                ticket.Reopen(TicketStatus.Assigned, reopenedAt);
                db.TicketSlaCycles.Add(TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), SlaCycleReason.Reopened, policy, reopenedAt));
                db.TicketActivities.Add(ticket.Record(TicketActivityType.Reopened, w.Manager.UserId, reopenedAt));
                totals.ReopenedCycles++;
                finished = false;
            }
        }
        else if (agents.Length > 0)
        {
            ticket.SetStatus(kind is 4 or 5 or 6 ? TicketStatus.Assigned : TicketStatus.InProgress, created.AddMinutes(1));
        }

        if (!finished)
        {
            totals.Open++;
            totals.OpenAssignments += agents.Length;
            if (agents.Length == 0)
            {
                totals.OpenUnassigned++;
            }

            var due = kind == 17 ? created.AddMinutes(30 + ((i * 37) % 3000) + 10 + policy.ResolutionMinutes) : cycle.ResolutionDueAtUtc;
            if (due < now)
            {
                totals.OpenOverdue++;
            }
        }
    }
}
