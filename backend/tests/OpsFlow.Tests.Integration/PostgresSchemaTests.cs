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

    private async Task ExecAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Database.ExecuteSqlRawAsync(sql);
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

    private static string InsertUser(string normalizedEmail, string status = "Active") =>
        $"INSERT INTO \"Users\" (\"Id\",\"Email\",\"NormalizedEmail\",\"DisplayName\",\"PasswordHash\",\"Status\",\"FailedLoginCount\",\"AuthVersion\",\"CreatedAtUtc\",\"UpdatedAtUtc\") VALUES ('{Guid.NewGuid()}','{normalizedEmail}','{normalizedEmail}','x','hash','{status}',0,1,NOW(),NOW())";

    [PostgresOnlyFact]
    public async Task Users_enforce_unique_email_and_a_valid_status()
    {
        var email = $"pg-{Guid.NewGuid():N}@example.com";
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Database.ExecuteSqlRawAsync(InsertUser(email));
        }

        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertUser(email)));
        Assert.Equal(CheckViolation, await SqlStateAsync(InsertUser($"other-{email}", "Banned")));
    }

    [PostgresOnlyFact]
    public async Task Refresh_tokens_are_unique_by_hash_and_die_with_their_session()
    {
        var user = await ApiClient.RegisterAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var existing = await db.RefreshTokens.AsNoTracking().FirstAsync(t => db.UserSessions.Any(s => s.Id == t.SessionId && s.UserId == user.UserId));

        var duplicate = $"INSERT INTO \"RefreshTokens\" (\"Id\",\"SessionId\",\"TokenHash\",\"CreatedAtUtc\",\"ExpiresAtUtc\") VALUES ('{Guid.NewGuid()}','{existing.SessionId}','{existing.TokenHash}',NOW(),NOW())";
        var orphan = $"INSERT INTO \"RefreshTokens\" (\"Id\",\"SessionId\",\"TokenHash\",\"CreatedAtUtc\",\"ExpiresAtUtc\") VALUES ('{Guid.NewGuid()}','{Guid.NewGuid()}','{Guid.NewGuid():N}',NOW(),NOW())";

        Assert.Equal(UniqueViolation, await SqlStateAsync(duplicate));
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(orphan));
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

    // ---- Milestone 2 tables: tenant-consistent composite FKs, uniqueness and check constraints as PostgreSQL enforces them ----

    private static string InsertTicket(Guid tenantId, long number, Guid categoryId, Guid createdBy, string status = "Open") =>
        $"INSERT INTO \"Tickets\" (\"Id\",\"TenantId\",\"Number\",\"Title\",\"Description\",\"Priority\",\"CategoryId\",\"CreatedByUserId\",\"Status\",\"CreatedAtUtc\",\"UpdatedAtUtc\",\"Version\",\"ActivitySequence\",\"CycleCount\",\"MessageSequence\") VALUES ('{Guid.NewGuid()}','{tenantId}',{number},'t','d','Low','{categoryId}','{createdBy}','{status}',NOW(),NOW(),1,0,0,0)"; // UPDATED: naya MessageSequence column NOT NULL hai

    private static string InsertAssignment(Guid tenantId, Guid ticketId, Guid membershipId, Guid byMembershipId) =>
        $"INSERT INTO \"TicketAssignments\" (\"Id\",\"TenantId\",\"TicketId\",\"MembershipId\",\"AssignedByMembershipId\",\"AssignedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenantId}','{ticketId}','{membershipId}','{byMembershipId}',NOW())";

    private static string InsertCycle(Guid tenantId, Guid ticketId, Guid policyId, int number, bool finished = false) =>
        $"INSERT INTO \"TicketSlaCycles\" (\"Id\",\"TenantId\",\"TicketId\",\"CycleNumber\",\"Reason\",\"SlaPolicyId\",\"ResponseMinutes\",\"ResolutionMinutes\",\"StartedAtUtc\",\"ResolutionDueAtUtc\",\"ResolvedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenantId}','{ticketId}',{number},'Reopened','{policyId}',60,120,NOW(),NOW(),{(finished ? "NOW()" : "NULL")})";

    [PostgresOnlyFact]
    public async Task Tickets_cannot_reference_another_tenants_category_or_duplicate_a_number()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticket = await a.CreateTicketAsync(a.Manager);

        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(InsertTicket(a.Org.Id, 900, b.CategoryId, a.Owner.UserId))); // composite (TenantId, CategoryId)
        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertTicket(a.Org.Id, ticket.Summary.Number, a.CategoryId, a.Owner.UserId))); // (TenantId, Number)
        Assert.Equal(CheckViolation, await SqlStateAsync(InsertTicket(a.Org.Id, 901, a.CategoryId, a.Owner.UserId, "Banished")));
        Assert.Equal(CheckViolation, await SqlStateAsync(InsertTicket(a.Org.Id, 0, a.CategoryId, a.Owner.UserId)));
    }

    [PostgresOnlyFact]
    public async Task Assignments_enforce_same_tenant_agents_and_a_single_active_row_per_agent()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticket = await a.CreateTicketAsync(a.Manager, new { title = "Assigned", description = "d", categoryId = a.CategoryId, assigneeMembershipIds = new[] { a.Agent1Membership } });

        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(InsertAssignment(a.Org.Id, ticket.Id, b.Agent1Membership, a.ManagerMembership))); // agent of another tenant
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(InsertAssignment(b.Org.Id, ticket.Id, b.Agent1Membership, b.ManagerMembership))); // ticket of another tenant
        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertAssignment(a.Org.Id, ticket.Id, a.Agent1Membership, a.ManagerMembership))); // partial unique: one ACTIVE row
    }

    [PostgresOnlyFact]
    public async Task A_ticket_can_never_have_two_active_sla_cycles_or_a_cycle_of_another_tenants_policy()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticket = await a.CreateTicketAsync(a.Manager);
        var activeCycle = (await a.SlaAsync(ticket.Id)).Current!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var policyOfA = await db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == a.Org.Id).Select(p => p.Id).FirstAsync();
        var policyOfB = await db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == b.Org.Id).Select(p => p.Id).FirstAsync();

        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertCycle(a.Org.Id, ticket.Id, policyOfA, 2))); // second ACTIVE cycle (partial unique index)
        Assert.Equal(UniqueViolation, await SqlStateAsync(InsertCycle(a.Org.Id, ticket.Id, policyOfA, activeCycle.CycleNumber))); // duplicate cycle number
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(InsertCycle(a.Org.Id, ticket.Id, policyOfB, 3, finished: true))); // composite (TenantId, SlaPolicyId)
    }

    [PostgresOnlyFact]
    public async Task Customer_links_and_category_names_are_unique_per_tenant_in_the_database()
    {
        var a = await TicketWorld.CreateAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var contact = await db.Customers.IgnoreQueryFilters().FirstAsync(c => c.TenantId == a.Org.Id);

        string Customer(string email, Guid? user) =>
            $"INSERT INTO \"Customers\" (\"Id\",\"TenantId\",\"Name\",\"Email\",\"NormalizedEmail\",\"UserId\",\"IsActive\",\"CreatedAtUtc\",\"UpdatedAtUtc\") VALUES ('{Guid.NewGuid()}','{a.Org.Id}','n','{email}','{email}',{(user is null ? "NULL" : $"'{user}'")},TRUE,NOW(),NOW())";

        Assert.Equal(UniqueViolation, await SqlStateAsync(Customer(contact.NormalizedEmail, null)));
        Assert.Equal(UniqueViolation, await SqlStateAsync(Customer($"other-{Guid.NewGuid():N}@example.com", contact.UserId))); // one contact per linked user
        Assert.Equal(UniqueViolation, await SqlStateAsync(
            $"INSERT INTO \"TicketCategories\" (\"Id\",\"TenantId\",\"Name\",\"NormalizedName\",\"IsActive\",\"CreatedAtUtc\",\"UpdatedAtUtc\") VALUES ('{Guid.NewGuid()}','{a.Org.Id}','general','general',TRUE,NOW(),NOW())"));
    }

    // ---- Milestone 3 tables ----

    [PostgresOnlyFact]
    public async Task Chat_notes_attachments_and_notifications_are_tenant_consistent_unique_and_checked()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticketA = await a.CreateTicketAsync(a.Customer);
        var sender = a.Customer.UserId;
        var client = Guid.NewGuid();

        string Message(Guid tenant, Guid ticket, int seq, string content, Guid clientId) =>
            $"INSERT INTO \"TicketMessages\" (\"Id\",\"TenantId\",\"TicketId\",\"Sequence\",\"SenderUserId\",\"SenderIsStaff\",\"Content\",\"ClientMessageId\",\"CreatedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenant}','{ticket}',{seq},'{sender}',FALSE,'{content}','{clientId}',NOW())";
        string Note(Guid tenant, Guid ticket, int seq, Guid clientId) =>
            $"INSERT INTO \"TicketNotes\" (\"Id\",\"TenantId\",\"TicketId\",\"Sequence\",\"AuthorUserId\",\"Content\",\"ClientNoteId\",\"CreatedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenant}','{ticket}',{seq},'{sender}','n','{clientId}',NOW())";
        string Notification(Guid tenant, Guid ticket, string key) =>
            $"INSERT INTO \"Notifications\" (\"Id\",\"TenantId\",\"UserId\",\"Type\",\"TicketId\",\"Subject\",\"DedupKey\",\"CreatedAtUtc\") VALUES ('{Guid.NewGuid()}','{tenant}','{sender}','NoteAdded','{ticket}','s','{key}',NOW())";

        await ExecAsync(Message(a.Org.Id, ticketA.Id, 1, "hi", client));
        Assert.Equal(UniqueViolation, await SqlStateAsync(Message(a.Org.Id, ticketA.Id, 1, "dup position", Guid.NewGuid())));
        Assert.Equal(UniqueViolation, await SqlStateAsync(Message(a.Org.Id, ticketA.Id, 2, "dup client id", client)));
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(Message(b.Org.Id, ticketA.Id, 3, "other tenant", Guid.NewGuid()))); // composite (TenantId, TicketId)
        Assert.Equal(CheckViolation, await SqlStateAsync(Message(a.Org.Id, ticketA.Id, 4, " ", Guid.NewGuid())));
        Assert.Equal(CheckViolation, await SqlStateAsync(Message(a.Org.Id, ticketA.Id, 0, "zero", Guid.NewGuid())));

        await ExecAsync(Note(a.Org.Id, ticketA.Id, 1, client));
        Assert.Equal(UniqueViolation, await SqlStateAsync(Note(a.Org.Id, ticketA.Id, 1, Guid.NewGuid())));
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(Note(b.Org.Id, ticketA.Id, 2, Guid.NewGuid())));

        await ExecAsync(Notification(a.Org.Id, ticketA.Id, "k1"));
        Assert.Equal(UniqueViolation, await SqlStateAsync(Notification(a.Org.Id, ticketA.Id, "k1")));
        Assert.Equal(ForeignKeyViolation, await SqlStateAsync(Notification(b.Org.Id, ticketA.Id, "k2")));
    }
}
