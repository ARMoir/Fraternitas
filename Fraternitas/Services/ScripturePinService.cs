using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Saves verified Douay-Rheims verses as calendar pins, discussion topics,
/// or replies without changing the existing SQLite schema.
/// Never accepts Bible text from the browser: re-fetches from the
/// Catholic-canon-checked Bible service before persisting any pin.
/// </summary>
public sealed class ScripturePinService(
    IDbContextFactory<FraternityDb> dbFactory,
    CatholicBibleService bible)
{
    public async Task<Guid> PinToCalendarAsync(
        ClaimsPrincipal user, string reference, DateTime date, string? reflection = null)
    {
        if (!user.IsInRole("Admin"))
            throw new UnauthorizedAccessException("Only group administrators may pin events to the calendar.");

        if (date.Year is < 2000 or > 2100)
            throw new ArgumentException("Please choose a valid calendar date.");

        var note = ValidateReflection(reflection);
        var text = await VerifiedVerseAsync(reference);
        await using var db = await dbFactory.CreateDbContextAsync();
        _ = await RequireMemberAsync(db, user);

        var day = date.Date;
        var pin = new CalendarEvent
        {
            GroupId = 1,
            Category = "Scripture",
            Title = $"📖 {reference}",
            Scripture = reference,
            Description = ComposeQuote(reference, text, note),
            StartLocal = day,
            EndLocal = day.AddDays(1)
        };
        db.CalendarEvents.Add(pin);
        await db.SaveChangesAsync();
        return pin.Id;
    }

    public async Task<Guid> PinToDiscussionAsync(
        ClaimsPrincipal user, string reference, string? reflection = null)
    {
        var note = ValidateReflection(reflection);
        var text = await VerifiedVerseAsync(reference);
        await using var db = await dbFactory.CreateDbContextAsync();
        var userId = await RequireMemberAsync(db, user);

        var post = new Discussion
        {
            GroupId = 1,
            AuthorId = userId,
            Title = $"📖 Scripture · {reference}",
            Body = ComposeQuote(reference, text, note)
        };
        db.Discussions.Add(post);
        await db.SaveChangesAsync();
        return post.Id;
    }

    public async Task<Guid> PinToReplyAsync(
        ClaimsPrincipal user, Guid discussionId, string reference, string? reflection = null)
    {
        var note = ValidateReflection(reflection);
        var text = await VerifiedVerseAsync(reference);
        await using var db = await dbFactory.CreateDbContextAsync();
        var userId = await RequireMemberAsync(db, user);

        if (!await db.Discussions.AnyAsync(d => d.Id == discussionId && d.GroupId == 1))
            throw new ArgumentException("This discussion is unavailable.");

        var reply = new Reply
        {
            DiscussionId = discussionId,
            AuthorId = userId,
            Body = ComposeQuote(reference, text, note)
        };
        db.Replies.Add(reply);
        await db.SaveChangesAsync();
        return reply.Id;
    }

    private static string ValidateReflection(string? reflection)
    {
        var note = (reflection ?? "").Trim();
        if (note.Length > 1200)
            throw new ArgumentException("Please limit your reflection to 1,200 characters.");
        return note;
    }

    private async Task<string> VerifiedVerseAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100)
            throw new ArgumentException("Choose a Scripture verse from the Catholic readings.");

        var passage = await bible.GetPassageAsync(reference);
        if (!passage.IsAvailable || passage.Verses.Count != 1)
            throw new InvalidOperationException(
                "The Catholic verse could not be verified right now. Please try again later.");

        return passage.Verses[0].Text;
    }

    private static string ComposeQuote(string reference, string text, string note)
    {
        var content = $"{reference} · Douay–Rheims Bible (1899, Catholic)\n\n“{text}”";
        if (note.Length != 0)
            content += $"\n\nReflection:\n{note}";
        return content;
    }

    private static async Task<string> RequireMemberAsync(FraternityDb db, ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)
            || !await db.Users.AnyAsync(member => member.Id == userId && member.GroupId == 1))
            throw new UnauthorizedAccessException("Sign in with a Fraternitas group account.");

        return userId;
    }
}
