using OpsFlow.Application.Analytics;

namespace OpsFlow.Tests.Unit;

public class ReportingTimeZoneIdTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Rome")]
    [InlineData("Asia/Karachi")]
    [InlineData("America/New_York")]
    [InlineData("  Europe/Rome  ")]
    public void Valid_iana_ids_are_accepted_and_trimmed(string id)
    {
        Assert.True(ReportingTimeZoneId.TryValidate(id, out var value, out _));
        Assert.Equal(id.Trim(), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("W. Europe Standard Time")] // Windows id: not portable
    [InlineData("europe/rome")] // exact spelling required
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("UTC+5")]
    [InlineData("Europe/Rome'; --")]
    [InlineData("../etc/passwd")]
    public void Invalid_ids_are_rejected_with_a_message(string? id)
    {
        Assert.False(ReportingTimeZoneId.TryValidate(id, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Ids_longer_than_the_column_are_rejected() =>
        Assert.False(ReportingTimeZoneId.TryValidate("Europe/" + new string('A', 70), out _, out _));
}
