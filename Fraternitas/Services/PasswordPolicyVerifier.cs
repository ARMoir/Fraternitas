using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;

namespace Fraternitas.Services;

// Run only against a disposable database.
public static class PasswordPolicyVerifier
{
    public static async Task VerifyAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        static Member NewUser()
        {
            var email = $"password-test-{Guid.NewGuid():N}@example.invalid";
            return new Member { UserName = email, Email = email, DisplayName = "Password test", GroupId = 1 };
        }

        foreach (var (password, valid) in new[] { ("abc1", false), ("abcdef", false), ("abcde1", true), ("ABCDE1", true), ("111111", true) })
        {
            var result = await users.CreateAsync(NewUser(), password);
            if (result.Succeeded != valid || PasswordPolicy.IsValid(password) != valid)
                throw new Exception("Password policy creation validation did not match expectations.");
        }

        // Simulate an existing password hash from before the policy changed.
        var legacy = NewUser();
        legacy.PasswordHash = users.PasswordHasher.HashPassword(legacy, "xy");
        if (!(await users.CreateAsync(legacy)).Succeeded || !await users.CheckPasswordAsync(legacy, "xy"))
            throw new Exception("Existing passwords must remain usable.");

        foreach (var invalid in new[] { "ab1", "abcdef" })
        {
            var reset = await users.GeneratePasswordResetTokenAsync(legacy);
            if ((await users.ResetPasswordAsync(legacy, reset, invalid)).Succeeded)
                throw new Exception("Password reset accepted a password below the new requirements.");
            if (!await users.CheckPasswordAsync(legacy, "xy"))
                throw new Exception("Rejected reset changed the existing password.");
        }
        var token = await users.GeneratePasswordResetTokenAsync(legacy);
        if (!(await users.ResetPasswordAsync(legacy, token, "abcde1")).Succeeded
            || !await users.CheckPasswordAsync(legacy, "abcde1")
            || await users.CheckPasswordAsync(legacy, "xy"))
            throw new Exception("Valid reset did not replace the old password.");

        Console.WriteLine("Verified 6-character/one-digit policy, no other complexity requirements, existing password compatibility and reset validation.");
    }
}
