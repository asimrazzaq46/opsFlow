using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>The safety rails around the opt-in disposable PostgreSQL server (no database needed).</summary>
public sealed class TestDatabaseGuardTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("db.internal.example.com", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_server_on_this_machine_is_accepted(string? host, bool allowed) =>
        Assert.Equal(allowed, TestDatabase.IsLoopbackHost(host));

    [Theory]
    [InlineData("opsflow_test_0123456789abcdef0123456789abcdef", true)]
    [InlineData("opsflow_hf_0123456789abcdef0123456789abcdef", true)]
    [InlineData("opsflow_startup_0123456789abcdef0123456789abcdef", true)]
    [InlineData("opsflow", false)] // the development database
    [InlineData("postgres", false)]
    [InlineData("opsflow_test_", false)]
    [InlineData("opsflow_test_0123456789abcdef0123456789abcdeg", false)]
    [InlineData("opsflow_test_0123456789abcdef0123456789abcdef; DROP DATABASE opsflow", false)]
    [InlineData("OPSFLOW_TEST_0123456789ABCDEF0123456789ABCDEF", false)]
    public void Only_generated_test_database_names_can_be_created_or_dropped(string name, bool allowed)
    {
        Assert.Equal(allowed, TestDatabase.IsTestDatabaseName(name));
        if (!allowed)
        {
            Assert.Throws<InvalidOperationException>(() => TestDatabase.DropPostgresDatabase(name));
            Assert.Throws<InvalidOperationException>(() => TestDatabase.CreatePostgresDatabase(name));
        }
    }
}
