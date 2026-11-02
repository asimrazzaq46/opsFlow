using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;

namespace OpsFlow.Tests.Unit;

public class DisplayNameRulesTests
{
    [Theory]
    [InlineData("  Ada Lovelace ", "Ada Lovelace")]
    [InlineData("Zoë Ñúñez", "Zoë Ñúñez")]
    public void Valid_names_are_trimmed(string input, string expected) =>
        Assert.Equal(expected, DisplayNameRules.Validate(input, "f"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("Tab\tName")]
    [InlineData("Right‮ToLeft")]
    public void Invalid_names_are_rejected(string? input) =>
        Assert.Equal(400, Assert.Throws<AppException>(() => DisplayNameRules.Validate(input, "f")).StatusCode);

    [Fact]
    public void Too_long_names_are_rejected() =>
        Assert.Throws<AppException>(() => DisplayNameRules.Validate(new string('x', 101), "f"));

    [Theory]
    [InlineData("Gina Google", "gina@gmail.com", "Gina Google")]
    [InlineData(null, "gina.g@gmail.com", "gina.g")]
    [InlineData("​", "x@gmail.com", "OpsFlow user")]
    public void External_names_fall_back_to_the_email_local_part_and_never_throw(string? name, string email, string expected) =>
        Assert.Equal(expected, DisplayNameRules.FromExternal(name, email));

    [Fact]
    public void External_names_are_cut_to_the_maximum_length() =>
        Assert.Equal(DisplayNameRules.MaxLength, DisplayNameRules.FromExternal(new string('n', 300), "a@gmail.com").Length);
}
