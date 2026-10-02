namespace TruvoID.API.Endpoints;

/// <summary>
/// Server-side rules for sign-up and password fields. The React forms mirror these
/// (web/src/validation.ts) for instant feedback, but these are the ones that count.
/// </summary>
public static class AuthValidation
{
    public static string? ValidateRegistration(string? institutionName, string? adminFullName, string? adminEmail, string? password)
    {
        var name = institutionName?.Trim() ?? "";
        if (name.Length < 2 || name.Length > 120)
            return "Institution name must be between 2 and 120 characters.";
        var adminName = adminFullName?.Trim() ?? "";
        if (adminName.Length < 2 || adminName.Length > 120)
            return "Enter the administrator's full name.";
        if (!IsValidEmail(adminEmail))
            return "Enter a valid administrator email address.";
        return ValidatePassword(password);
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Password must be at least 8 characters.";
        if (password.Length > 128)
            return "Password must be at most 128 characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Password must include at least one letter and one number.";
        return null;
    }

    public static bool IsValidEmail(string? email)
    {
        var value = email?.Trim() ?? "";
        var at = value.IndexOf('@');
        return value.Length <= 254 && at > 0 && at == value.LastIndexOf('@')
            && value.IndexOf('.', at) > at + 1 && !value.EndsWith('.') && !value.Any(char.IsWhiteSpace);
    }
}
