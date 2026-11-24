using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

public sealed record MemberAccessRow(
    string Id, string DisplayName, string Email, bool IsAdmin, bool IsMemberOnly);

/// <summary>
/// Member is the default. Preserve existing administrator roles and explicit
/// Member-only claims; startup must never grant administrator access.
/// No schema migration is required for existing SQLite databases.
/// </summary>
public sealed class MemberAccessService(IServiceScopeFactory scopes)
{
    public const string MemberOnlyClaimType = "Fraternitas.MemberOnly";

    public async Task<List<MemberAccessRow>> GetMembersAsync()
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var members = await users.Users.Where(u => u.GroupId == 1)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync();
        var result = new List<MemberAccessRow>();
        foreach (var member in members)
        {
            var claims = await users.GetClaimsAsync(member);
            result.Add(new MemberAccessRow(
                member.Id, member.DisplayName, member.Email ?? "",
                await users.IsInRoleAsync(member, "Admin"),
                !await users.IsInRoleAsync(member, "Admin") ||
                claims.Any(c => c.Type == MemberOnlyClaimType && c.Value == "true")));
        }
        return result;
    }

    public async Task<bool> IsGroupMemberAsync(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)) return false;
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var user = await users.FindByIdAsync(userId);
        return user is not null && user.GroupId == 1;
    }

    public async Task<bool> CanManageEventAsync(ClaimsPrincipal principal, CalendarEvent ev)
    {
        if (ev.GroupId != 1) return false;
        if (await IsAdministratorAsync(principal)) return true;
        return ev.CreatedByUserId is not null
            && ev.CreatedByUserId == principal.FindFirstValue(ClaimTypes.NameIdentifier)
            && await IsGroupMemberAsync(principal);
    }

    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal principal)
    {
        if (!principal.IsInRole("Admin")) return false;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.GroupId != 1
            || !await users.IsInRoleAsync(user, "Admin"))
            return false;

        return !(await users.GetClaimsAsync(user)).Any(c =>
            c.Type == MemberOnlyClaimType && c.Value == "true");
    }

    public async Task SetMemberOnlyAsync(ClaimsPrincipal actor, string userId, bool memberOnly)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
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
        }

    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
