using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Security;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// The session rule is shared by the per-request check and the live-connection batch check. These tests only generate SQL (no connection is
/// opened), so the PostgreSQL translation stays verified even where no PostgreSQL is available to run the integration suite against.
/// </summary>
public sealed class SessionValidationSqlTests
{
    private static OpsFlowDbContext PostgresContext() =>
        new(new DbContextOptionsBuilder<OpsFlowDbContext>().UseNpgsql("Host=localhost;Database=never-opened").Options, new TenantContext());

    [Fact]
    public void The_per_request_check_is_one_statement_over_active_sessions_and_active_users()
    {
        using var db = PostgresContext();
        var (user, session) = (Guid.NewGuid(), Guid.NewGuid());

        var sql = SessionValidator
            .ValidSessions(db, DateTime.UtcNow)
            .Where(v => v.SessionId == session && v.UserId == user && v.AuthVersion == 3)
            .ToQueryString();

        Assert.Contains("\"RevokedAtUtc\" IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"ExpiresAtUtc\" >", sql, StringComparison.Ordinal);
        Assert.Contains("JOIN", sql, StringComparison.Ordinal);
        Assert.Contains("\"AuthVersion\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_of_sessions_is_one_array_lookup_not_one_query_per_connection()
    {
        using var db = PostgresContext();
        List<Guid> sessions = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        var sql = SessionValidator
            .ValidSessions(db, DateTime.UtcNow)
            .Where(v => sessions.Contains(v.SessionId))
            .ToQueryString();

        Assert.Contains("ANY", sql, StringComparison.Ordinal);
        Assert.Contains("\"RevokedAtUtc\" IS NULL", sql, StringComparison.Ordinal);
        Assert.Equal(1, sql.Split("SELECT", StringSplitOptions.None).Length - 1); // a single statement
    }
}
