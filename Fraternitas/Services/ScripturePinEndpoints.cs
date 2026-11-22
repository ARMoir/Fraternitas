using System.Globalization;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Native, CSRF-protected Catholic verse pinning. ScripturePinService fetches
/// the verified Douay–Rheims verse server-side; posted browser text is never trusted.
/// </summary>
public static class ScripturePinEndpoints
{
    public static void MapScripturePinEndpoints(this WebApplication app)
    {
        app.MapPost("/scripture/pin", async (
            HttpContext context, IAntiforgery antiforgery,
            ScripturePinService pins, ILoggerFactory loggers) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest("Invalid Scripture pin submission.");
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest("The form expired. Reload the reading and try again.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var reference = form["reference"].ToString();
            var reflection = form["reflection"].ToString();
            var mode = form["mode"].ToString();

            Guid? discussionId = null;
            if (!string.IsNullOrWhiteSpace(form["discussionId"].ToString()))
            {
                if (!Guid.TryParse(form["discussionId"], out var id))
                    return Results.Redirect("/readings?pin=invalid");
                discussionId = id;
            }

            try
            {
                if (mode == "discussion")
                {
                    if (discussionId.HasValue)
                    {
                        await pins.PinToReplyAsync(context.User, discussionId.Value,
                            reference, reflection);
                        return Results.Redirect($"/discussions/{discussionId.Value}");
                    }

                    var id = await pins.PinToDiscussionAsync(context.User, reference, reflection);
                    return Results.Redirect($"/discussions/{id}");
                }

                if (mode == "calendar")
                {
                    if (!DateTime.TryParseExact(form["calendarDate"].ToString(),
                            "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date))
                        return Results.Redirect("/readings?pin=invalid");

                    var id = await pins.PinToCalendarAsync(
                        context.User, reference, date, reflection);
                    return Results.Redirect($"/calendar#event-{id:N}");
                }

                return Results.Redirect("/readings?pin=invalid");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or UnauthorizedAccessException or DbUpdateException)
            {
                loggers.CreateLogger("ScripturePin")
                    .LogWarning(ex, "Could not complete Catholic Scripture pin submission");
                return Results.Redirect(discussionId.HasValue
                    ? $"/discussions/{discussionId.Value}?pin=failed#bible-browser"
                    : "/discussions?pin=failed#bible-browser");
            }
        }).RequireAuthorization();
    }
}
