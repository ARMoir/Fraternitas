using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;

namespace Fraternitas.Services;

/// <summary>
/// Run only with a disposable FRATERNITAS_DATA_DIR. Verifies that a member's
/// calendar preference survives fresh service scopes and remains per-account.
/// </summary>
public static class UserPreferenceVerifier
{
    public static async Task VerifyAsync(IServiceProvider services)
    {
        using var setupScope = services.CreateScope();
        var users = setupScope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var unique = Guid.NewGuid().ToString("N");

        static void Ensure(IdentityResult result)
        {
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        var first = new Member
        {
            UserName = $"preference-a-{unique}@example.invalid",
            Email = $"preference-a-{unique}@example.invalid",
            DisplayName = "Preference Test A"
        };
        var second = new Member
        {
            UserName = $"preference-b-{unique}@example.invalid",
            Email = $"preference-b-{unique}@example.invalid",
            DisplayName = "Preference Test B"
        };

        Ensure(await users.CreateAsync(first));
        Ensure(await users.CreateAsync(second));

        static ClaimsPrincipal Principal(string id) =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test"));

        var firstPrincipal = Principal(first.Id);
        var secondPrincipal = Principal(second.Id);

        async Task<bool> ReadInFreshScopeAsync(ClaimsPrincipal principal)
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<UserPreferencesService>()
                .GetBooleanAsync(principal, UserPreferencesService.CalendarShowDailyPrayer, true);
        }

        if (!await ReadInFreshScopeAsync(firstPrincipal) || !await ReadInFreshScopeAsync(secondPrincipal))
            throw new InvalidOperationException("Calendar prayer must be shown by default.");

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserPreferencesService>()
                .SetBooleanAsync(firstPrincipal, UserPreferencesService.CalendarShowDailyPrayer, false, true);
        }

        if (await ReadInFreshScopeAsync(firstPrincipal))
            throw new InvalidOperationException("Calendar preference did not persist after disabling.");
        if (!await ReadInFreshScopeAsync(secondPrincipal))
            throw new InvalidOperationException("Calendar preference leaked to a different user.");

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserPreferencesService>()
                .SetBooleanAsync(firstPrincipal, UserPreferencesService.CalendarShowDailyPrayer, true, true);
        }

        if (!await ReadInFreshScopeAsync(firstPrincipal))
            throw new InvalidOperationException("Calendar preference did not persist after enabling.");

        var prefs = setupScope.ServiceProvider.GetRequiredService<UserPreferencesService>();
        foreach (var key in new[] { UserPreferencesService.CalendarShowUsHolidays, UserPreferencesService.CalendarShowCatholicHolidays }.Concat(UserPreferencesService.CalendarEventCategories.Select(UserPreferencesService.CategoryPreference)))
        {
            if (!await prefs.GetBooleanAsync(firstPrincipal, key, true)) throw new Exception("Holidays should default on.");
            await prefs.SetBooleanAsync(firstPrincipal, key, false, true);
            if (await prefs.GetBooleanAsync(firstPrincipal, key, true) || !await prefs.GetBooleanAsync(secondPrincipal, key, true))
                throw new Exception("Holiday opt-out not isolated.");
            await prefs.SetBooleanAsync(firstPrincipal, key, true, true);
            if (!await prefs.GetBooleanAsync(firstPrincipal, key, true)) throw new Exception("Holiday preference not restored.");
        }

        Console.WriteLine("Calendar filters verified: all default on, saved opt-outs across scopes, isolated per member.");
    }
}
