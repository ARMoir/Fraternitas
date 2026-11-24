namespace Fraternitas.Services;

public static class PasswordPolicy
{
    public const int MinimumLength = 6;

    public static bool IsValid(string password) =>
        password.Length >= MinimumLength && password.Any(c => c is >= '0' and <= '9');
}
