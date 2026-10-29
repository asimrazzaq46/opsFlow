using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Constraints and indexes as PostgreSQL actually enforces them (schema from EnsureCreated, not migrations).</summary>
public class PostgresSchemaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";

    private async Task<string> SqlStateAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
        return Assert.IsType<PostgresException>(ex is PostgresException ? ex : ex.InnerException).SqlState;
    }

    private static string InsertMembership(Guid tenantId, Guid userId, string role) =>
        $"INSERT INTO \"Memberships\" (\"Id\",\"TenantId\",\"UserId\",\"Role\",\"Status\",\"CreatedAtUtc\",\"UpdatedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenantId}','{userId}','{role}','Active',NOW(),NOW())";

    private static string InsertInvitation(Guid tenantId, Guid inviterMembershipId, string email, string tokenHash) =>
        $"INSERT INTO \"Invitations\" (\"Id\",\"TenantId\",\"Email\",\"NormalizedEmail\",\"Role\",\"TokenHash\",\"InvitedByMembershipId\",\"CreatedAtUtc\",\"ExpiresAtUtc\") VALUES ('{Guid.NewGuid()}','{tenantId}','{email}','{email}','Agent','{tokenHash}','{inviterMembershipId}',NOW(),NOW())";

    [PostgresOnlyFact]
    public async Task Role_check_constraint_rejects_unknown_roles()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();

        Assert.Equal(CheckViolation, await SqlStateAsync(InsertMembership(org.Id, Guid.NewGuid(), "Hacker")));
    }

    [PostgresOnlyFact]
    public async Task A_user_can_only_have_one_membership_per_tenant()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();

        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertMembership(org.Id, owner.UserId, "Agent")));
    }

    [PostgresOnlyFact]
    public async Task Partial_unique_index_allows_only_one_open_invitation_per_email()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var inviter = (await owner.MembersAsync(org.Id)).Single();
        var email = $"open-{Guid.NewGuid():N}@example.com";
        (await owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email, role = "Agent" })).EnsureSuccessStatusCode();

        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertInvitation(org.Id, inviter.Id, email, "dup-open-hash")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'Invitations'").ToListAsync();
        Assert.Contains(indexes, d => d.Contains("UNIQUE", StringComparison.Ordinal) && d.Contains("WHERE", StringComparison.Ordinal));
    }

    [PostgresOnlyFact]
    public async Task Composite_foreign_key_rejects_an_inviter_from_another_tenant()
    {
        var ownerA = await ApiClient.RegisterAsync(factory);
        var orgA = await ownerA.CreateOrganizationAsync("PG A");
        var ownerB = await ApiClient.RegisterAsync(factory);
        var orgB = await ownerB.CreateOrganizationAsync("PG B");
        var foreignInviter = (await ownerB.MembersAsync(orgB.Id)).Single();

        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(InsertInvitation(orgA.Id, foreignInviter.Id, "fk@example.com", "fk-hash")));
    }

    [PostgresOnlyFact]
    public async Task Timestamps_are_stored_as_timestamptz()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        var types = await db.Database.SqlQueryRaw<string>(
            "SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'Memberships' AND column_name = 'CreatedAtUtc'").ToListAsync();

        Assert.Equal("timestamp with time zone", Assert.Single(types));
    }
}
