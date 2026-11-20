using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Conventional HTTP forms for posting fraternity topics and replies.
/// A websocket failure must not silently discard a member's writing.
/// </summary>
public static class DiscussionActionEndpoints
{
    public static void MapDiscussionActionEndpoints(this WebApplication app)
    {
        app.MapPost("/discussions/create", async (
            HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory) =>
        {
            if (!await ValidateAsync(context, antiforgery))
                return Results.BadRequest("Invalid request. Reload Discussions and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var title = form["title"].ToString().Trim();
            var message = form["body"].ToString().Trim();
            if (title.Length is < 1 or > 150 || message.Length is < 1 or > 6000)
                return Results.Redirect("/discussions?error=invalid");

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            await using var db = await factory.CreateDbContextAsync();
            if (string.IsNullOrWhiteSpace(userId)
                || !await db.Users.AnyAsync(u => u.Id == userId && u.GroupId == 1))
                return Results.Forbid();

            var post = new Discussion
            {
                GroupId = 1,
                Title = title,
                Body = message,
                AuthorId = userId
            };
            db.Discussions.Add(post);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/discussions/{post.Id}");
        }).RequireAuthorization();

        app.MapPost("/discussions/{id:guid}/reply", async (
            Guid id, HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory) =>
        {
            if (!await ValidateAsync(context, antiforgery))
                return Results.BadRequest("Invalid request. Reload the discussion and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var message = form["message"].ToString().Trim();
            if (message.Length is < 1 or > 3000)
                return Results.Redirect($"/discussions/{id}?error=invalid");

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            await using var db = await factory.CreateDbContextAsync();
            if (string.IsNullOrWhiteSpace(userId)
                || !await db.Users.AnyAsync(u => u.Id == userId && u.GroupId == 1))
                return Results.Forbid();
            if (!await db.Discussions.AnyAsync(d => d.Id == id && d.GroupId == 1))
                return Results.NotFound();

            db.Replies.Add(new Reply
            {
                DiscussionId = id,
                Body = message,
                AuthorId = userId
            });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/discussions/{id}");
        }).RequireAuthorization();
    }

    private static async Task<bool> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
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
}
