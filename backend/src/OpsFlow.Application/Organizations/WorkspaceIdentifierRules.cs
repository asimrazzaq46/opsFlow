using System.Text.RegularExpressions;

namespace OpsFlow.Application.Organizations;

/// <summary>
/// Format of the workspace identifiers that can only be chosen at creation. Values are normalized (slug lower-case, prefix upper-case)
/// before they are checked or stored, so the unique slug index and ticket numbers always see one canonical spelling.
/// </summary>
public static partial class WorkspaceIdentifierRules
{
    public const int SlugMinLength = 2;
    public const int SlugMaxLength = 50;
    public const int PrefixMinLength = 2;
    public const int PrefixMaxLength = 10;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugShape();

    [GeneratedRegex("^[A-Z][A-Z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixShape();

    public static bool TryNormalizeSlug(string? input, out string slug, out string error)
    {
        slug = input?.Trim().ToLowerInvariant() ?? string.Empty;
        error = string.Empty;
        if (slug.Length is < SlugMinLength or > SlugMaxLength)
        {
            error = $"Use {SlugMinLength} to {SlugMaxLength} characters.";
            return false;
        }

        if (!SlugShape().IsMatch(slug))
        {
            error = "Use lowercase letters, digits and single hyphens; start and end with a letter or digit.";
            return false;
        }

        return true;
    }

    public static bool TryNormalizePrefix(string? input, out string prefix, out string error)
    {
        prefix = input?.Trim().ToUpperInvariant() ?? string.Empty;
        error = string.Empty;
        if (prefix.Length is < PrefixMinLength or > PrefixMaxLength)
        {
            error = $"Use {PrefixMinLength} to {PrefixMaxLength} characters.";
            return false;
        }

        if (!PrefixShape().IsMatch(prefix))
        {
            error = "Use letters and digits only, starting with a letter.";
            return false;
        }

        return true;
    }
}
