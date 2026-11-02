using OpsFlow.Application.Common;

namespace OpsFlow.Application.Auth;

/// <summary>Shared display-name rules: trimmed, 2-100 characters, no control or invisible formatting characters.</summary>
public static class DisplayNameRules
{
    public const int MinLength = 2;
    public const int MaxLength = 100;

    public static string Validate(string? value, string field)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length is < MinLength or > MaxLength)
        {
            throw AppException.Validation(field, $"Use {MinLength} to {MaxLength} characters.");
        }

        if (name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
        {
            throw AppException.Validation(field, "Control or invisible formatting characters are not allowed.");
        }

        return name;
    }

    /// <summary>Best-effort name from an external profile; falls back to the email's local part. Never throws.</summary>
    public static string FromExternal(string? name, string email)
    {
        foreach (var candidate in new[] { name, email.Split('@')[0] })
        {
            var trimmed = new string((candidate ?? string.Empty).Trim().Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            trimmed = trimmed.Length > MaxLength ? trimmed[..MaxLength].Trim() : trimmed;
            if (trimmed.Length >= MinLength)
            {
                return trimmed;
            }
        }

        return "OpsFlow user";
    }
}
