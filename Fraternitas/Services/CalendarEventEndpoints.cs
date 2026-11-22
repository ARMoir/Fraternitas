using System.Globalization;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Form-based calendar event management. Works from a plain HTTP browser
/// without a Blazor Server circuit. The persistence routes always re-check
/// administrator access and validate antiforgery tokens.
/// </summary>
public static class CalendarEventEndpoints
{
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "Meeting", "Fasting", "Prayer", "Fellowship", "Scripture", "Other" };
    private const string LocalDateFormat = "yyyy-MM-ddTHH:mm";

    public static void MapCalendarEventEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup("/calendar/events")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        endpoints.MapPost("/create", async (
            HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory,
            MemberAccessService access) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateFormAsync(context, antiforgery))
                return Results.BadRequest("Invalid or expired form token. Reload the calendar.");

            await using var db = await factory.CreateDbContextAsync();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var input = await ParseAsync(form, db);
            var calendarUrl = CalendarReturnUrl(form);
            if (input is null)
            {
                // Preserve the chosen day and hour when the form needs correcting.
                var slot = DateTime.TryParseExact(form["slot"].ToString(), LocalDateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var selectedSlot)
                    && selectedSlot.Year is >= 2000 and <= 2100
                    ? $"&slot={selectedSlot:yyyy-MM-ddTHH:mm}" : "";
                return Results.Redirect($"{calendarUrl}&create=invalid{slot}#create-event");
            }

            var ev = new CalendarEvent { GroupId = 1 };
            Apply(ev, input);
            db.CalendarEvents.Add(ev);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"{calendarUrl}#event-{ev.Id:N}");
        });

        endpoints.MapPost("/{id:guid}/update", async (
            Guid id, HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory,
            MemberAccessService access) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateFormAsync(context, antiforgery))
                return Results.BadRequest("Invalid or expired form token. Reload the editor.");

            await using var db = await factory.CreateDbContextAsync();
            var ev = await db.CalendarEvents
                .FirstOrDefaultAsync(e => e.Id == id && e.GroupId == 1);
            if (ev is null)
                return Results.NotFound();

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var input = await ParseAsync(form, db);
            var fromCalendar = form["calendarModal"].ToString() == "true";
            var calendarUrl = CalendarReturnUrl(form);
            if (input is null)
                return Results.Redirect(fromCalendar
                    ? $"{calendarUrl}&edit={id:N}&editError=invalid#edit-event"
                    : $"/events/{id}/edit?error=invalid");

            Apply(ev, input);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect(fromCalendar
                ? $"{calendarUrl}#event-{id:N}"
                : $"/calendar#event-{id:N}");
        });

        endpoints.MapPost("/{id:guid}/delete", async (
            Guid id, HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory,
            MemberAccessService access) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateFormAsync(context, antiforgery))
                return Results.BadRequest("Invalid or expired form token. Reload the editor.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["confirmDelete"].ToString() != "yes")
                return Results.Redirect($"/events/{id}/edit?error=confirm");

            await using var db = await factory.CreateDbContextAsync();
            var ev = await db.CalendarEvents
                .FirstOrDefaultAsync(e => e.Id == id && e.GroupId == 1);
            if (ev is null)
                return Results.NotFound();

            // Retain the associated group discussions and resources when an
            // event is deleted, matching the previous interactive editor.
            foreach (var discussion in await db.Discussions
                         .Where(d => d.GroupId == 1 && d.EventId == id).ToListAsync())
                discussion.EventId = null;
            foreach (var document in await db.SharedDocuments
                         .Where(d => d.GroupId == 1 && d.EventId == id).ToListAsync())
                document.EventId = null;

            db.CalendarEvents.Remove(ev);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect("/calendar?event=deleted");
        });
    }

    private static async Task<bool> ValidateFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType)
            return false;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static string CalendarReturnUrl(IFormCollection form)
    {
        var view = form["calendarView"].ToString();
        if (view is not ("day" or "week" or "month"))
            view = "month";
        var selectedDate = DateTime.TryParseExact(form["calendarDate"].ToString(),
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date.Year is >= 2000 and <= 2100
            ? date : GroupClock.Now.Date;
        return $"/calendar?view={view}&date={selectedDate:yyyy-MM-dd}";
    }

    private sealed record EventInput(
        string Title, DateTime StartLocal, DateTime EndLocal,
        string Category, string Description, string Scripture,
        string MeetingUrl, int? ReadingId);

    private static async Task<EventInput?> ParseAsync(
        IFormCollection form, FraternityDb db)
    {
        var title = form["title"].ToString().Trim();
        var category = form["category"].ToString().Trim();
        var scripture = form["scripture"].ToString().Trim();
        var description = form["description"].ToString().Trim();
        var meetingUrl = form["meetingUrl"].ToString().Trim();

        if (title.Length is < 1 or > 150 || !Categories.Contains(category)
            || scripture.Length > 100 || description.Length > 5000
            || meetingUrl.Length > 1000
            || !DateTime.TryParseExact(form["startLocal"].ToString(), LocalDateFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateTime.TryParseExact(form["endLocal"].ToString(), LocalDateFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end <= start)
            return null;

        if (meetingUrl.Length != 0
            && (!Uri.TryCreate(meetingUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
            return null;

        int? readingId = null;
        var readingValue = form["readingId"].ToString();
        if (!string.IsNullOrWhiteSpace(readingValue))
        {
            if (!int.TryParse(readingValue, out var id)
                || !await db.Readings.AnyAsync(r => r.Id == id))
                return null;
            readingId = id;
        }

        return new EventInput(title, start, end, category,
            description, scripture, meetingUrl, readingId);
    }

    private static void Apply(CalendarEvent ev, EventInput input)
    {
        ev.Title = input.Title;
        ev.StartLocal = input.StartLocal;
        ev.EndLocal = input.EndLocal;
        ev.Category = input.Category;
        ev.Description = input.Description;
        ev.Scripture = input.Scripture;
        ev.MeetingUrl = input.MeetingUrl;
        ev.ReadingId = input.ReadingId;
    }
}
