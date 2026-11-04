namespace OpsFlow.Application.Auth;

/// <summary>Shared display-name rules: trimmed, 2-100 characters, no control or invisible formatting characters.</summary>
public static class DisplayNameRules
{
    public const int MinLength = 2;
    public const int MaxLength = 100;

    /// <summary>True with the trimmed name when it is acceptable; otherwise false with a client-safe message.</summary>
    public static bool TryValidate(string? value, out string name, out string error)
    {
        name = value?.Trim() ?? string.Empty;
        error = string.Empty;
        if (name.Length is < MinLength or > MaxLength)
        {
            error = $"Use {MinLength} to {MaxLength} characters.";
            return false;
        }

        if (name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
        {
            error = "Control or invisible formatting characters are not allowed.";
            return false;
        }

        return true;
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
