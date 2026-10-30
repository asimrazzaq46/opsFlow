using OpsFlow.Application.Tickets;

namespace OpsFlow.Tests.Unit;

public class AttachmentValidatorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("A.PNG", "image/png")]
    public void Matching_extension_and_signature_yield_the_server_decided_type(string name, string expected) =>
        Assert.Equal(expected, AttachmentValidator.Verify(name, Png));

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a.png.exe")]
    [InlineData("a")]
    [InlineData("a.svg")] // active content is not on the allow-list
    [InlineData("a.html")]
    public void Extensions_outside_the_allow_list_are_rejected(string name) => Assert.Null(AttachmentValidator.Verify(name, Png));

    [Fact]
    public void A_renamed_executable_fails_the_signature_check() => Assert.Null(AttachmentValidator.Verify("photo.png", [0x4D, 0x5A, 0x90, 0x00]));

    [Fact]
    public void Text_must_be_valid_utf8_without_nul_bytes()
    {
        Assert.Equal("text/plain", AttachmentValidator.Verify("n.txt", "grüße"u8.ToArray()));
        Assert.Null(AttachmentValidator.Verify("n.txt", [0x41, 0x00, 0x42]));
        Assert.Null(AttachmentValidator.Verify("n.txt", [0xFF, 0xFE, 0xFD]));
    }

    [Theory]
    [InlineData("..\\..\\evil\\x.png", "x.png")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\temp\\a b.pdf", "a b.pdf")]
    [InlineData("na\0me?.txt", "na_me_.txt")]
    [InlineData("..", "")]
    public void File_names_are_reduced_to_a_safe_display_name(string input, string expected) =>
        Assert.Equal(expected, AttachmentValidator.SanitizeFileName(input));

    [Fact]
    public void Very_long_names_are_shortened_but_keep_their_extension() =>
        Assert.EndsWith(".png", AttachmentValidator.SanitizeFileName(new string('a', 400) + ".png"), StringComparison.Ordinal);
}
