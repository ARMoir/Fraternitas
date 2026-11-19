using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

public sealed record MemberAccessRow(
    string Id, string DisplayName, string Email, bool IsAdmin, bool IsMemberOnly);

/// <summary>
/// Admin is the default for every group member. An explicit "Member only"
/// decision is persisted as an ASP.NET Core Identity user claim, so the
/// default-role reconciliation cannot silently undo a downgrade.
/// No schema migration is required for existing SQLite databases.
/// </summary>
public sealed class MemberAccessService(UserManager<Member> users)
{
    public const string MemberOnlyClaimType = "Fraternitas.MemberOnly";

    public async Task<List<MemberAccessRow>> GetMembersAsync()
    {
        var members = await users.Users.Where(u => u.GroupId == 1)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync();
        var result = new List<MemberAccessRow>();
        foreach (var member in members)
        {
            var claims = await users.GetClaimsAsync(member);
            result.Add(new MemberAccessRow(
                member.Id, member.DisplayName, member.Email ?? "",
                await users.IsInRoleAsync(member, "Admin"),
                claims.Any(c => c.Type == MemberOnlyClaimType && c.Value == "true")));
        }
        return result;
    }

    public async Task SetMemberOnlyAsync(ClaimsPrincipal actor, string userId, bool memberOnly)
    {
        if (!actor.IsInRole("Admin"))
            throw new UnauthorizedAccessException("Only administrators can change access.");

        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(actorId))
            throw new UnauthorizedAccessException("Please sign in first.");

        var actingUser = await users.FindByIdAsync(actorId);
        if (actingUser is null || actingUser.GroupId != 1
            || !await users.IsInRoleAsync(actingUser, "Admin"))
            throw new UnauthorizedAccessException("Administrator access has been removed. Sign in again.");

        var member = await users.FindByIdAsync(userId);
        if (member is null || member.GroupId != 1)
            throw new ArgumentException("Group member not found.");

        var claims = await users.GetClaimsAsync(member);
        var exception = claims.FirstOrDefault(c =>
            c.Type == MemberOnlyClaimType && c.Value == "true");

        if (memberOnly)
        {
            if (await users.IsInRoleAsync(member, "Admin"))
            {
                // A group must retain at least one administrator.
                var groupUsers = await users.Users.Where(u => u.GroupId == 1).ToListAsync();
                int otherAdmins = 0;
                foreach (var candidate in groupUsers.Where(u => u.Id != member.Id))
                    if (await users.IsInRoleAsync(candidate, "Admin"))
                        otherAdmins++;
                if (otherAdmins == 0)
                    throw new InvalidOperationException("The group must keep at least one administrator.");
            }

            if (exception is null)
                RequireSuccess(await users.AddClaimAsync(member,
                    new Claim(MemberOnlyClaimType, "true")));

            if (await users.IsInRoleAsync(member, "Admin"))
                RequireSuccess(await users.RemoveFromRoleAsync(member, "Admin"));
        }
        else
        {
            if (!await users.IsInRoleAsync(member, "Admin"))
                RequireSuccess(await users.AddToRoleAsync(member, "Admin"));

            if (exception is not null)
                RequireSuccess(await users.RemoveClaimAsync(member, exception));
        }

        if (!await users.IsInRoleAsync(member, "Member"))
            RequireSuccess(await users.AddToRoleAsync(member, "Member"));

        // Previously issued authentication cookies must be revalidated.
        RequireSuccess(await users.UpdateSecurityStampAsync(member));
    }

    public static async Task ReconcileDefaultsAsync(
        IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var members = await users.Users.Where(u => u.GroupId == 1).ToListAsync();

        int promoted = 0;
        foreach (var member in members)
        {
            if (!await users.IsInRoleAsync(member, "Member"))
                RequireSuccess(await users.AddToRoleAsync(member, "Member"));

            var memberOnly = (await users.GetClaimsAsync(member))
                .Any(c => c.Type == MemberOnlyClaimType && c.Value == "true");
            var isAdmin = await users.IsInRoleAsync(member, "Admin");

            if (memberOnly && isAdmin)
            {
                RequireSuccess(await users.RemoveFromRoleAsync(member, "Admin"));
                RequireSuccess(await users.UpdateSecurityStampAsync(member));
            }
            else if (!memberOnly && !isAdmin)
            {
                RequireSuccess(await users.AddToRoleAsync(member, "Admin"));
                RequireSuccess(await users.UpdateSecurityStampAsync(member));
                promoted++;
            }
        }

        if (promoted != 0)
            logger.LogInformation("Granted default Admin access to {Count} existing group members.", promoted);
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
