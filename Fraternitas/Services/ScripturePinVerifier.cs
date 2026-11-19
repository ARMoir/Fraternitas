using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace Fraternitas.Services;

/// <summary>
/// Temporary-database integration check for GitHub Actions.
/// Run ONLY with a disposable FRATERNITAS_DATA_DIR; the command writes test data.
/// </summary>
public static class ScripturePinVerifier
{
    public static async Task VerifyAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FraternityDb>>();
        var pins = scope.ServiceProvider.GetRequiredService<ScripturePinService>();

        var testId = Guid.NewGuid().ToString("N");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Users.Add(new Member
            {
                Id = testId,
                UserName = $"pin-test-{testId}@example.invalid",
                Email = $"pin-test-{testId}@example.invalid",
                DisplayName = "Scripture pin test member"
            });
            await db.SaveChangesAsync();
        }

        // Grant the test identity an actual stored Admin role. A fabricated
        // Admin claim alone must not permit calendar modifications.
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Member>>();
        var stored = await users.FindByIdAsync(testId)
            ?? throw new Exception("Test member was not created.");
        var assigned = await users.AddToRoleAsync(stored, "Admin");
        if (!assigned.Succeeded)
            throw new Exception("Could not create test administrator: " +
                string.Join("; ", assigned.Errors.Select(e => e.Description)));

        ClaimsPrincipal Principal(bool admin)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, testId) };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        var member = Principal(false);
        var admin = Principal(true);
        const string reference = "Luke 1:26";
        var date = new DateTime(2026, 11, 25);
        const string reflection = "A prayerful reminder to listen.";

        var discussionId = await pins.PinToDiscussionAsync(member, reference, reflection);
        var replyId = await pins.PinToReplyAsync(member, discussionId, reference);
        var eventId = await pins.PinToCalendarAsync(admin, reference, date, reflection);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var post = await db.Discussions.SingleAsync(x => x.Id == discussionId);
            var reply = await db.Replies.SingleAsync(x => x.Id == replyId);
            var ev = await db.CalendarEvents.SingleAsync(x => x.Id == eventId);

            if (post.GroupId != 1 || post.AuthorId != testId || !post.Body.Contains(reflection)
                || !post.Body.Contains("Douay–Rheims"))
                throw new Exception("New Scripture discussion did not persist the verified verse and reflection.");

            if (reply.DiscussionId != discussionId || reply.AuthorId != testId
                || !reply.Body.Contains(reference))
                throw new Exception("Scripture reply was not saved to the intended group discussion.");

            if (ev.Category != "Scripture" || ev.Scripture != reference
                || ev.StartLocal != date || ev.EndLocal != date.AddDays(1)
                || !ev.Description.Contains("Douay–Rheims"))
                throw new Exception("All-day calendar Scripture pin was not saved correctly.");
        }

        var denied = false;
        try
        {
            await pins.PinToCalendarAsync(member, reference, date);
        }
        catch (UnauthorizedAccessException)
        {
            denied = true;
        }

        if (!denied)
            throw new Exception("A non-admin member could pin Scripture to the calendar.");

        Console.WriteLine("Verified Catholic Scripture pin creation: discussion topic, reply, all-day calendar event, and admin-only calendar authorization.");
    }
}
