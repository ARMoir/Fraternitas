using System.Net;
using System.Net.Mail;
using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;

namespace Fraternitas.Services;

/// <summary>
/// Local-only bootstrap helpers. Web setup works in Development on loopback,
/// and only before the first account. Terminal recovery never deletes data.
/// </summary>
public static class AdminSetup
{
    public static bool IsLocalDevelopmentRequest(HttpContext context, IWebHostEnvironment environment) =>
        environment.IsDevelopment()
        && context.Connection.RemoteIpAddress is { } address
        && IPAddress.IsLoopback(address);

    public static bool ValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= 256
        && MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> RunConsoleSetupAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();

        Console.WriteLine();
        Console.WriteLine("FRATERNITAS ADMIN SETUP / RECOVERY");
        Console.WriteLine("This command runs in your local terminal.");
        Console.WriteLine("Your existing database and group content will not be deleted.");
        Console.Write("Admin email: ");
        var email = (Console.ReadLine() ?? "").Trim();
        if (!ValidEmail(email))
        {
            Console.Error.WriteLine("Please supply a valid email address.");
            return false;
        }

        Console.Write("New password (12+ characters): ");
        var password = ReadSecret();
        Console.Write("Confirm password: ");
        var confirmation = ReadSecret();
        if (password.Length < 12 || password != confirmation)
        {
            Console.Error.WriteLine("Passwords must match and contain at least 12 characters.");
            return false;
        }

        var user = await users.FindByEmailAsync(email);
        IdentityResult result;

        if (user is null)
        {
            user = new Member
            {
                UserName = email, Email = email, EmailConfirmed = true,
                GroupId = 1, DisplayName = "Administrator"
            };
            result = await users.CreateAsync(user, password);
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            result = await users.ResetPasswordAsync(user, token, password);
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine(error.Description);
            return false;
        }

        foreach (var role in new[] { "Admin", "Member" })
        {
            if (!await users.IsInRoleAsync(user, role))
            {
                result = await users.AddToRoleAsync(user, role);
                if (!result.Succeeded)
                {
                    foreach (var error in result.Errors)
                        Console.Error.WriteLine(error.Description);
                    return false;
                }
            }
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        Console.WriteLine("Admin ready. Start Fraternitas normally and sign in.");
        return true;
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(chars.ToArray());
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar) && chars.Count < 128)
                chars.Add(key.KeyChar);
        }
    }
}
