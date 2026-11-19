using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;

namespace Fraternitas.Services;

/// <summary>Runs with a disposable FRATERNITAS_DATA_DIR in GitHub Actions.</summary>
public static class MemberAccessVerifier
{
    public static async Task VerifyAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var access = scope.ServiceProvider.GetRequiredService<MemberAccessService>();

        var key = Guid.NewGuid().ToString("N");
        var actor = new Member
        {
            UserName = $"test-admin-{key}@example.invalid",
            Email = $"test-admin-{key}@example.invalid", DisplayName = "Test Administrator"
        };
        var existing = new Member
        {
            UserName = $"legacy-member-{key}@example.invalid",
            Email = $"legacy-member-{key}@example.invalid", DisplayName = "Legacy Member"
        };

        static void Check(IdentityResult result, string description)
        {
            if (!result.Succeeded)
                throw new Exception($"{description}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        // Use a fresh Identity context for every post-write assertion: reconciliation
        // and role changes intentionally run in independent scopes.
        async Task<(bool IsAdmin, bool MemberOnly)> StateAsync(string userId)
        {
            using var fresh = services.CreateScope();
            var currentUsers = fresh.ServiceProvider.GetRequiredService<UserManager<Member>>();
            var stored = await currentUsers.FindByIdAsync(userId)
                ?? throw new Exception("Test user is missing.");
            return (await currentUsers.IsInRoleAsync(stored, "Admin"),
                (await currentUsers.GetClaimsAsync(stored)).Any(c =>
                    c.Type == MemberAccessService.MemberOnlyClaimType && c.Value == "true"));
        }

        Check(await users.CreateAsync(actor), "Create admin");
        Check(await users.AddToRoleAsync(actor, "Member"), "Add Member role to admin");
        Check(await users.AddToRoleAsync(actor, "Admin"), "Add Admin role to admin");

        Check(await users.CreateAsync(existing), "Create legacy member");
        Check(await users.AddToRoleAsync(existing, "Member"), "Add legacy Member role");
        if (await users.IsInRoleAsync(existing, "Admin"))
            throw new Exception("Test legacy member should initially lack the Admin role.");

        // Upgrade users that were invited before Admin became the default.
        await MemberAccessService.ReconcileDefaultsAsync(services, logger);
        if (!(await StateAsync(existing.Id)).IsAdmin)
            throw new Exception("Existing Member was not promoted to default Admin on startup.");

        var actorPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actor.Id),
             new Claim(ClaimTypes.Role, "Admin")], "Test"));

        // Deliberately set one user to Member only.
        await access.SetMemberOnlyAsync(actorPrincipal, existing.Id, memberOnly: true);
        var downgraded = await StateAsync(existing.Id);
        if (downgraded.IsAdmin || !downgraded.MemberOnly)
            throw new Exception($"Member-only override was not saved: Admin={downgraded.IsAdmin}, explicit={downgraded.MemberOnly}.");

        // Re-running startup must NOT silently re-grant Admin.
        await MemberAccessService.ReconcileDefaultsAsync(services, logger);
        if ((await StateAsync(existing.Id)).IsAdmin)
            throw new Exception("Startup incorrectly promoted an explicitly downgraded account.");

        // Prevent demoting the final administrator.
        var prevented = false;
        try
        {
            await access.SetMemberOnlyAsync(actorPrincipal, actor.Id, memberOnly: true);
        }
        catch (InvalidOperationException)
        {
            prevented = true;
        }
        if (!prevented)
            throw new Exception("Application allowed the last administrator to be downgraded.");

        // Re-enable Admin and remove the explicit override.
        await access.SetMemberOnlyAsync(actorPrincipal, existing.Id, memberOnly: false);
        var restored = await StateAsync(existing.Id);
        if (!restored.IsAdmin || restored.MemberOnly)
            throw new Exception("Administrator restoration did not remove the explicit exception.");

        // A stale cookie must not have authority to modify roles after demotion.
        await access.SetMemberOnlyAsync(actorPrincipal, existing.Id, memberOnly: true);
        var stalePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, existing.Id),
             new Claim(ClaimTypes.Role, "Admin")], "StaleCookie"));
        var staleDenied = false;
        try
        {
            await access.SetMemberOnlyAsync(stalePrincipal, actor.Id, memberOnly: true);
        }
        catch (UnauthorizedAccessException)
        {
            staleDenied = true;
        }
        if (!staleDenied)
            throw new Exception("Demoted user with stale Admin cookie was allowed to change roles.");

        Console.WriteLine("Default-admin access verified: legacy upgrade, persistent Member-only override, restoration, last-admin protection and stale-cookie rejection.");
    }
}
