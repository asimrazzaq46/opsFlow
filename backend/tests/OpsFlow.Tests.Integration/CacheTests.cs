using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure;
using OpsFlow.Infrastructure.Caching;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Cache-aside of tenant categories / SLA policies through the real services (in-memory cache with the Redis generation semantics).</summary>
public class CacheTests(CachingApiFactory factory) : IClassFixture<CachingApiFactory>
{
    [Fact]
    public async Task Categories_are_read_from_the_cache_until_a_committed_change_invalidates_them()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var url = w.Url("ticket-categories");

        await w.Owner.GetAsync(url);
        var loads = factory.Cache.Loads;
        var cached = await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(url));
        Assert.Equal(loads, factory.Cache.Loads); // hit: PostgreSQL was not queried

        (await w.Owner.PostAsync(url, new { name = "Hardware" })).EnsureSuccessStatusCode();
        var fresh = await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(url));

        Assert.Equal(cached.Count + 1, fresh.Count);
        Assert.Contains(fresh, c => c.Name == "Hardware");
        Assert.True(factory.Cache.Loads > loads);
    }

    [Fact]
    public async Task Cached_values_are_isolated_per_tenant_and_authorization_still_applies()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        (await a.Owner.PostAsync(a.Url("ticket-categories"), new { name = "OnlyInTenantA" })).EnsureSuccessStatusCode();

        var forA = await a.Owner.ReadAsync<List<CategoryJson>>(await a.Owner.GetAsync(a.Url("ticket-categories")));
        var forB = await b.Owner.ReadAsync<List<CategoryJson>>(await b.Owner.GetAsync(b.Url("ticket-categories")));

        Assert.Contains(forA, c => c.Name == "OnlyInTenantA");
        Assert.DoesNotContain(forB, c => c.Name == "OnlyInTenantA");
        await a.Owner.GetAsync(a.Url("sla-policies")); // warm the SLA policy cache ...
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Customer.GetAsync(a.Url("sla-policies"))).StatusCode); // ... a customer still cannot read it
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url("sla-policies"))).StatusCode); // nor a foreign tenant's owner
    }

    [Fact]
    public async Task A_changed_sla_policy_is_visible_immediately()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var policies = await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(w.Url("sla-policies")));
        var urgent = policies.Single(p => p.Priority == "Urgent");

        (await w.Owner.PutAsync(w.Url($"sla-policies/{urgent.Id}"), new { responseMinutes = 30, resolutionMinutes = 240 })).EnsureSuccessStatusCode();

        var after = (await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(w.Url("sla-policies")))).Single(p => p.Priority == "Urgent");
        Assert.Equal(30, after.ResponseMinutes);
    }

    [Fact]
    public async Task Tenant_defaults_initialization_refreshes_cached_categories_and_policies_only_when_it_changed_something()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var categories = w.Url("ticket-categories");
        var policies = w.Url("sla-policies");
        await w.Owner.GetAsync(categories);
        await w.Owner.GetAsync(policies); // both lists are cached now
        // simulate an older tenant that lacks some defaults (direct DB change: nothing invalidates the cache)
        await factory.WithDbAsync(db => db.TicketCategories.IgnoreQueryFilters().Where(c => c.TenantId == w.Org.Id && c.Name == "Billing").ExecuteDeleteAsync());
        await factory.WithDbAsync(db => db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == w.Org.Id && p.Priority == OpsFlow.Domain.Tickets.TicketPriority.Low).ExecuteDeleteAsync());
        Assert.Contains(await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(categories)), c => c.Name == "Billing"); // stale hit, as expected

        (await w.Owner.PostAsync(w.Url("defaults"))).EnsureSuccessStatusCode();

        var refreshed = await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(categories));
        Assert.Contains(refreshed, c => c.Name == "Billing");
        Assert.Equal(4, (await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(policies))).Count);
        Assert.Equal(2, (await factory.OutboxAsync(w.Org.Id, "cache.invalidated")).Count); // durable backstop for both resources

        var loads = factory.Cache.Loads;
        (await w.Owner.PostAsync(w.Url("defaults"))).EnsureSuccessStatusCode(); // nothing is missing any more
        await w.Owner.GetAsync(categories);
        await w.Owner.GetAsync(policies);
        Assert.Equal(loads, factory.Cache.Loads); // no needless invalidation
        Assert.Equal(2, (await factory.OutboxAsync(w.Org.Id, "cache.invalidated")).Count);
    }

    [Fact]
    public async Task A_lost_post_commit_invalidation_is_repaired_by_the_outbox()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.Owner.GetAsync(w.Url("ticket-categories"));
        (await w.Owner.PostAsync(w.Url("ticket-categories"), new { name = "Late" })).EnsureSuccessStatusCode();

        var events = await factory.OutboxAsync(w.Org.Id, "cache.invalidated");

        Assert.NotEmpty(events); // committed in the same transaction as the category
        await factory.DrainOutboxAsync();
        Assert.All(await factory.OutboxAsync(w.Org.Id, "cache.invalidated"), m => Assert.Equal(OpsFlow.Domain.Outbox.OutboxStatus.Completed, m.Status));
    }
}

/// <summary>Redis behaviour. The outage test needs nothing; the live tests need Redis: set OPSFLOW_TEST_REDIS (e.g. localhost:6380 from docker compose).</summary>
public class RedisTenantCacheTests
{
    private static readonly string[] Db = ["db"];
    private static readonly string[] Old = ["old"];
    private static readonly string[] New = ["new"];
    private static readonly string? LiveRedis = Environment.GetEnvironmentVariable("OPSFLOW_TEST_REDIS");

    private static RedisTenantCache Create(string connection) =>
        new(Options.Create(new CacheOptions { RedisConnectionString = connection }), NullLogger<RedisTenantCache>.Instance, TimeProvider.System);

    private sealed class LiveRedisFactAttribute : FactAttribute
    {
        public LiveRedisFactAttribute()
        {
            if (string.IsNullOrEmpty(LiveRedis))
            {
                Skip = "Set OPSFLOW_TEST_REDIS to run against a real Redis.";
            }
        }
    }

    [Fact]
    public async Task An_unreachable_redis_falls_back_to_the_database_and_never_throws()
    {
        using var cache = Create("127.0.0.1:1");
        var tenant = Guid.NewGuid();

        var value = await cache.GetOrCreateAsync(tenant, CacheResources.Categories, _ => Task.FromResult(Db), CancellationToken.None);

        Assert.Equal(Db, value);
        Assert.False(await cache.InvalidateAsync(tenant, CacheResources.Categories, CancellationToken.None)); // reported, so the outbox retries it
    }

    [Fact]
    public async Task A_full_connection_string_with_options_is_accepted_and_an_unreachable_server_still_falls_back()
    {
        using var cache = Create("127.0.0.1:1,password=secret,connectRetry=1");

        var value = await cache.GetOrCreateAsync(Guid.NewGuid(), CacheResources.Categories, _ => Task.FromResult(Db), CancellationToken.None);

        Assert.Equal(Db, value);
    }

    [Fact]
    public void A_malformed_redis_connection_string_fails_the_startup_with_a_clear_message_instead_of_failing_reads()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u",
            ["Cache:RedisConnectionString"] = "host:notaport,,,ssl=maybe=",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => _ = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddInfrastructure(configuration));

        Assert.Contains("Cache:RedisConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [LiveRedisFact]
    public async Task Miss_hit_invalidate_and_tenant_isolation_against_real_redis()
    {
        using var cache = Create(LiveRedis!);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var loads = 0;
        Task<string[]> Load(CancellationToken _) => Task.FromResult<string[]>([$"v{++loads}"]);

        Assert.Equal(["v1"], await cache.GetOrCreateAsync(a, CacheResources.SlaPolicies, Load, CancellationToken.None)); // miss -> database
        Assert.Equal(["v1"], await cache.GetOrCreateAsync(a, CacheResources.SlaPolicies, Load, CancellationToken.None)); // hit
        Assert.Equal(["v2"], await cache.GetOrCreateAsync(b, CacheResources.SlaPolicies, Load, CancellationToken.None)); // another tenant has its own entry
        Assert.True(await cache.InvalidateAsync(a, CacheResources.SlaPolicies, CancellationToken.None));
        Assert.Equal(["v3"], await cache.GetOrCreateAsync(a, CacheResources.SlaPolicies, Load, CancellationToken.None));
        Assert.Equal(["v2"], await cache.GetOrCreateAsync(b, CacheResources.SlaPolicies, Load, CancellationToken.None)); // b untouched
    }

    [LiveRedisFact]
    public async Task A_reader_that_loaded_stale_data_cannot_write_it_back_after_an_invalidation()
    {
        using var cache = Create(LiveRedis!);
        var tenant = Guid.NewGuid();

        // the invalidation happens while the reader is still loading the OLD value from the database
        var stale = await cache.GetOrCreateAsync(tenant, CacheResources.Categories, async _ =>
        {
            await cache.InvalidateAsync(tenant, CacheResources.Categories, CancellationToken.None);
            return Old;
        }, CancellationToken.None);

        var next = await cache.GetOrCreateAsync(tenant, CacheResources.Categories, _ => Task.FromResult(New), CancellationToken.None);

        Assert.Equal(["old"], stale);
        Assert.Equal(["new"], next); // the stale entry sits under the previous generation and is unreachable
    }
}
