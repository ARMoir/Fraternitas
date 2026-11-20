using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Per-account preferences saved in the existing ASP.NET Identity claims table.
/// No database migration is required for an already-deployed SQLite database.
/// Preferences are deliberately read from SQLite rather than the authentication
/// cookie so changes take effect when the user next opens the page.
/// </summary>
public sealed class UserPreferencesService(IDbContextFactory<FraternityDb> dbFactory)
{
    public const string CalendarShowDailyPrayer = "calendar.showDailyPrayer";
    private const string ClaimPrefix = "fraternitas.preference.";

    private static string CurrentUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("A signed-in user is required to access preferences.");

    public async Task<bool> GetBooleanAsync(ClaimsPrincipal user, string key, bool defaultValue = false)
    {
        var userId = CurrentUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.UserClaims.AsNoTracking()
            .Where(c => c.UserId == userId && c.ClaimType == ClaimPrefix + key)
            .Select(c => c.ClaimValue)
            .FirstOrDefaultAsync();
        return saved is null ? defaultValue : string.Equals(saved, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetBooleanAsync(ClaimsPrincipal user, string key, bool value)
    {
        var userId = CurrentUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var claims = await db.UserClaims
            .Where(c => c.UserId == userId && c.ClaimType == ClaimPrefix + key)
            .ToListAsync();

        if (value)
        {
            if (claims.Count == 0)
            {
                db.UserClaims.Add(new IdentityUserClaim<string>
                {
                    UserId = userId,
                    ClaimType = ClaimPrefix + key,
                    ClaimValue = "true"
                });
            }
            else
            {
                claims[0].ClaimValue = "true";
                if (claims.Count > 1)
                    db.UserClaims.RemoveRange(claims.Skip(1));
            }
        }
        else
        {
            // No claim means false, preserving the original default for new users.
            db.UserClaims.RemoveRange(claims);
        }

        await db.SaveChangesAsync();
    }
}


/// <summary>
/// A standard form endpoint makes calendar preferences work even when the Blazor
/// Server websocket is unavailable. Authentication is required, and the
/// antiforgery token is validated just like the site's sign-in forms.
/// </summary>
public static class UserPreferenceEndpoints
{
    public static void MapUserPreferenceEndpoints(this WebApplication app)
    {
        app.MapPost("/calendar/preferences/daily-prayer",
            async (HttpContext context, IAntiforgery antiforgery,
                UserPreferencesService preferences) =>
            {
                if (!context.Request.HasFormContentType)
                    return Results.BadRequest("Invalid calendar preference request.");

                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.BadRequest("Your session expired. Reload the calendar and try again.");
                }

                var form = await context.Request.ReadFormAsync();
                // When a checkbox is unchecked the browser omits its field.
                var enabled = form.TryGetValue("showDailyPrayer", out var value)
                    && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                await preferences.SetBooleanAsync(
                    context.User, UserPreferencesService.CalendarShowDailyPrayer, enabled);

                return Results.LocalRedirect("/calendar");
            }).RequireAuthorization();
    }
}
