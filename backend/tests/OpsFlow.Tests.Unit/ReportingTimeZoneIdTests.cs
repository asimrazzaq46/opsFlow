using OpsFlow.Application.Analytics;
using OpsFlow.Application.Common;

namespace OpsFlow.Tests.Unit;

public class ReportingTimeZoneIdTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Rome")]
    [InlineData("Asia/Karachi")]
    [InlineData("America/New_York")]
    [InlineData("  Europe/Rome  ")]
    public void Valid_iana_ids_are_accepted_and_trimmed(string id) => Assert.Equal(id.Trim(), ReportingTimeZoneId.Validate(id, "f"));

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
    public void Invalid_ids_are_rejected_with_a_field_validation_error(string? id)
    {
        var ex = Assert.Throws<AppException>(() => ReportingTimeZoneId.Validate(id, "ReportingTimeZone"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("ReportingTimeZone", ex.Errors!.Keys);
    }

    [Fact]
    public void Ids_longer_than_the_column_are_rejected() =>
        Assert.Throws<AppException>(() => ReportingTimeZoneId.Validate("Europe/" + new string('A', 70), "f"));
}
