using System.Net;
using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Admin actions handled by ordinary, antiforgery-validated form submissions.
/// This remains operational when the desktop Blazor Server circuit disconnects.
/// </summary>
public static class AdminActionEndpoints
{
    public static void MapAdminActionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        group.MapPost("/invitations/create", async (
            HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory, MemberAccessService access,
            IWebHostEnvironment environment, IConfiguration configuration) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateAsync(context, antiforgery))
                return Results.BadRequest("Invalid form. Reload Administration and try again.");

            var code = AccountEndpoints.NewToken();
            await using var db = await factory.CreateDbContextAsync();
            db.Invitations.Add(new Invitation
            {
                TokenSha256 = AccountEndpoints.Hash(code),
                GroupId = 1,
                ExpiresUtc = DateTime.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync(context.RequestAborted);

            // Avoid constructing a private invitation URL from an untrusted Host
            // header. Production uses the configured public site URL.
            var configuredOrigin = configuration["FRATERNITAS_PUBLIC_URL"];
            var origin = !string.IsNullOrWhiteSpace(configuredOrigin)
                ? configuredOrigin.TrimEnd('/')
                : environment.IsProduction()
                    ? "https://fraternitas.moir.pw"
                    : $"{context.Request.Scheme}://{context.Request.Host}";
            var url = origin + "/account/join?code=" + Uri.EscapeDataString(code);
            var safeUrl = WebUtility.HtmlEncode(url);

            context.Response.Headers.CacheControl = "no-store, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var html = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1">
                    <meta name="referrer" content="no-referrer">
                    <title>Invitation link | Fraternitas</title>
                    <link rel="stylesheet" href="/app.css">
                </head>
                <body>
                    <main class="main">
                        <div class="page-head">
                            <div class="eyebrow">ADMINISTRATION</div>
                            <h1>Group invitation created</h1>
                            <p>Share this private link only with the men you intend to invite.</p>
                        </div>
                        <section class="panel" style="max-width:820px">
                            <label for="invite-url">Invitation URL (valid for seven days)</label>
                            <input id="invite-url" type="text" readonly value="{safeUrl}"
                                   onclick="this.select()">
                            <p class="muted small">Copy this link now. For security, Fraternitas
                                saves only its hash and cannot display this link again.</p>
                            <p class="muted small">Anyone registering through this link receives
                                Administrator access by default.</p>
                            <a class="action" href="/admin">Return to Administration</a>
                        </section>
                    </main>
                </body>
                </html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapPost("/invitations/revoke", async (
            HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> factory, MemberAccessService access) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateAsync(context, antiforgery))
                return Results.BadRequest("Invalid form. Reload Administration and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["invitationId"], out var id))
                return Results.Redirect("/admin?status=failed");

            await using var db = await factory.CreateDbContextAsync();
            var invitation = await db.Invitations.FirstOrDefaultAsync(
                i => i.Id == id && i.GroupId == 1 && i.RedeemedUtc == null);
            if (invitation is null)
                return Results.Redirect("/admin?status=failed");
            invitation.RedeemedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect("/admin?status=invite-revoked");
        });

        group.MapPost("/members/role", async (
            HttpContext context, IAntiforgery antiforgery,
            MemberAccessService access, ILoggerFactory loggers) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await ValidateAsync(context, antiforgery))
                return Results.BadRequest("Invalid form. Reload Administration and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var target = form["userId"].ToString();
            if (string.IsNullOrWhiteSpace(target)
                || !bool.TryParse(form["memberOnly"], out var memberOnly))
                return Results.Redirect("/admin?status=failed");

            try
            {
                await access.SetMemberOnlyAsync(context.User, target, memberOnly);
                if (memberOnly
                    && string.Equals(context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        target, StringComparison.Ordinal))
                    return Results.Redirect("/account/logout");
                return Results.Redirect("/admin?status=role-saved");
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                loggers.CreateLogger("AdminAction")
                    .LogWarning(ex, "Administrator could not save member access");
                return Results.Redirect("/admin?status=failed");
            }
        });
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
