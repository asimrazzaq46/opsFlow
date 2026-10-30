using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tenants;

/// <summary>
/// The default records every tenant needs for the ticket workflow. ASSUMPTION (no approved ERD available): a minimal category
/// set and calendar-time SLA targets per priority. Tenant onboarding adds them in the SAME transaction as the tenant.
/// </summary>
public static class TenantDefaults
{
    public static readonly string[] CategoryNames = ["General", "Technical Issue", "Billing"];

    public static readonly IReadOnlyDictionary<TicketPriority, (int ResponseMinutes, int ResolutionMinutes)> SlaTargets = new Dictionary<TicketPriority, (int, int)>
    {
        [TicketPriority.Low] = (1440, 4320),
        [TicketPriority.Medium] = (480, 2880),
        [TicketPriority.High] = (240, 1440),
        [TicketPriority.Urgent] = (60, 480),
    };

    /// <summary>Adds whatever is missing (nothing is overwritten) and returns how many records were added. The caller saves.</summary>
    public static int AddMissing(IOpsFlowDbContext db, Guid tenantId, DateTime nowUtc, bool hasSettings, bool hasCounter, ISet<string> existingCategories, ISet<TicketPriority> existingPriorities)
    {
        var added = 0;
        if (!hasSettings)
        {
            db.Settings.Add(TenantSettings.Create(tenantId, nowUtc));
            added++;
        }

        if (!hasCounter)
        {
            db.TicketCounters.Add(TenantTicketCounter.Create(tenantId));
            added++;
        }

        foreach (var name in CategoryNames.Where(n => !existingCategories.Contains(TicketCategory.Normalize(n))))
        {
            db.TicketCategories.Add(TicketCategory.Create(tenantId, name, nowUtc));
            added++;
        }

        foreach (var (priority, targets) in SlaTargets.Where(p => !existingPriorities.Contains(p.Key)))
        {
            db.SlaPolicies.Add(SlaPolicy.Create(tenantId, priority, targets.ResponseMinutes, targets.ResolutionMinutes, nowUtc));
            added++;
        }

        return added;
    }

    /// <summary>Everything a brand-new tenant needs (used by onboarding, atomically with the tenant).</summary>
    public static void AddAll(IOpsFlowDbContext db, Guid tenantId, DateTime nowUtc) =>
        AddMissing(db, tenantId, nowUtc, false, false, new HashSet<string>(), new HashSet<TicketPriority>());
}

/// <summary>Explicit, idempotent initialization of tenants created before these defaults existed (e.g. OpsFlow Demo).</summary>
public sealed partial class TenantDefaultsService(IOpsFlowDbContext db, ITenantContext tenantContext, ITenantCache cache, OutboxWriter outbox, TimeProvider clock, ILogger<TenantDefaultsService> logger)
{
    /// <summary>
    /// Concurrent initializations of one tenant are SERIALIZED by first taking the tenant row lock (the update lasts until the
    /// transaction ends); the loser then sees the committed defaults and adds nothing. Racing inserts would otherwise end in
    /// unique violations or PostgreSQL deadlocks.
    /// </summary>
    public async Task<int> EnsureAsync(CancellationToken ct)
    {
        var tenantId = tenantContext.TenantId!.Value;
        await using var transaction = await db.BeginTransactionAsync(ct);
        var stamp = Guid.NewGuid();
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.OwnerChangeStamp, stamp), ct);

        var categories = (await db.TicketCategories.Select(c => c.NormalizedName).ToListAsync(ct)).ToHashSet();
        var priorities = (await db.SlaPolicies.Select(p => p.Priority).ToListAsync(ct)).ToHashSet();
        var added = TenantDefaults.AddMissing(db, tenantId, clock.GetUtcNow().UtcDateTime,
            await db.Settings.AnyAsync(ct), await db.TicketCounters.AnyAsync(ct), categories, priorities);
        if (added == 0)
        {
            return 0;
        }

        // UPDATED: cache sirf tab invalidate hota hai jab categories/SLA policies sach mein add hui (settings/counter cached nahi)
        var categoriesChanged = CategoryNamesMissing(categories);
        var policiesChanged = TenantDefaults.SlaTargets.Keys.Any(p => !priorities.Contains(p));
        if (categoriesChanged)
        {
            outbox.StageCacheInvalidation(tenantId, CacheResources.Categories);
        }

        if (policiesChanged)
        {
            outbox.StageCacheInvalidation(tenantId, CacheResources.SlaPolicies);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (categoriesChanged)
        {
            await cache.InvalidateAsync(tenantId, CacheResources.Categories, CancellationToken.None); // commit ke baad; outbox event backstop hai
        }

        if (policiesChanged)
        {
            await cache.InvalidateAsync(tenantId, CacheResources.SlaPolicies, CancellationToken.None);
        }

        LogInitialized(logger, tenantId, added);
        return added;
    }

    private static bool CategoryNamesMissing(HashSet<string> existing) => TenantDefaults.CategoryNames.Any(n => !existing.Contains(TicketCategory.Normalize(n)));

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId} defaults initialized ({Added} records)")]
    private static partial void LogInitialized(ILogger logger, Guid tenantId, int added);
}
