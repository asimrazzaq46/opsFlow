namespace OpsFlow.Application.Auth;

public static class PasswordPolicy
{
    public const int MinLength = 7;
    public const int MaxLength = 128;

    public static IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            errors.Add($"Password must be at least {MinLength} characters long.");
        }

        if (password is { Length: > MaxLength })
        {
            errors.Add($"Password must be at most {MaxLength} characters long.");
        }

        if (password is not null)
        {
            //if (!password.Any(char.IsLower))
            //{
            //    errors.Add("Password must contain a lowercase letter.");
            //}

            //if (!password.Any(char.IsUpper))
            //{
            //    errors.Add("Password must contain an uppercase letter.");
            //}

            //if (!password.Any(char.IsDigit))
            //{
            //    errors.Add("Password must contain a digit.");
            //}

            //if (password.All(char.IsLetterOrDigit))
            //{
            //    errors.Add("Password must contain a special character.");
            //}
        }

        return errors;
    }
}
