using System.Globalization;
using System.Net;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Password recovery for a small private group with no outbound mail server.
/// Members request a reset on the sign-in page; a current administrator creates
/// a private, one-use link and sends it to them. Uses existing AspNetUserTokens
/// (no migration, no changes to users, calendar or discussions).
/// </summary>
public static class PasswordRecoveryEndpoints
{
    public const string Provider = "FraternitasPasswordRecovery";
    public const string RequestName = "Requested";
    public const string LinkName = "ResetLink";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(2);

    public static void MapPasswordRecoveryEndpoints(this WebApplication app)
    {
        app.MapGet("/account/forgot-password", (HttpContext context, IAntiforgery antiforgery) =>
        {
            NoStore(context);
            var token = WebUtility.HtmlEncode(antiforgery.GetAndStoreTokens(context).RequestToken ?? "");
            return Results.Content(AccountEndpoints.Page("Forgot password", $"""
                <h1>Forgot your password?</h1>
                <p>Enter the email address you registered with. A group administrator will
                   see your request and can send you a password reset link.</p>
                <form method="post" action="/account/forgot-password">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <label>Email address
                    <input name="email" type="email" maxlength="256" autocomplete="email" required>
                  </label>
                  <button type="submit">Request password reset</button>
                </form>
                <p class="muted">You won't receive an automatic email. Ask a group administrator
                   to share the reset link privately.</p>
                <p><a href="/account/login">Back to sign in</a></p>
                """), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/forgot-password", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<Member> users, FraternityDb db) =>
        {
            NoStore(context);
            if (!await AccountEndpoints.ValidForm(context, antiforgery))
                return Results.BadRequest("Invalid request. Reload the page and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString().Trim();
            if (AdminSetup.ValidEmail(email))
            {
                var member = await users.FindByEmailAsync(email);
                if (member is not null && member.GroupId == 1)
                {
                    var token = await db.Set<IdentityUserToken<string>>().FirstOrDefaultAsync(t =>
                        t.UserId == member.Id && t.LoginProvider == Provider &&
                        t.Name == RequestName, context.RequestAborted);

                    var now = DateTime.UtcNow;
                    if (token is null)
                    {
                        db.Set<IdentityUserToken<string>>().Add(new IdentityUserToken<string>
                        {
                            UserId = member.Id, LoginProvider = Provider,
                            Name = RequestName, Value = now.ToString("O")
                        });
                        await db.SaveChangesAsync(context.RequestAborted);
                    }
                    else if (!DateTime.TryParse(token.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var requested)
                        || now - requested > TimeSpan.FromMinutes(10))
                    {
                        token.Value = now.ToString("O");
                        await db.SaveChangesAsync(context.RequestAborted);
                    }
                }
            }
            // Do not disclose whether the entered email belongs to a group member.
            return Results.Content(AccountEndpoints.Page("Reset requested", """
                <h1>Request received</h1>
                <p>If that email belongs to a member, a group administrator can now create
                   a private reset link for you from the Administration page.</p>
                <p>Ask an administrator to send the link directly to you.</p>
                <p><a href="/account/login">Return to sign in</a></p>
                """), "text/html");
        }).AllowAnonymous();

        app.MapPost("/admin/members/password-reset", async (
            HttpContext context, IAntiforgery antiforgery,
            MemberAccessService access, FraternityDb db,
            IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await AccountEndpoints.ValidForm(context, antiforgery))
                return Results.BadRequest("Invalid form. Reload Administration and try again.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var memberId = form["userId"].ToString();
            if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 128)
                return Results.Redirect("/admin?status=failed");

            var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(m =>
                m.Id == memberId && m.GroupId == 1, context.RequestAborted);
            if (member is null)
                return Results.Redirect("/admin?status=failed");

            // Each new link automatically replaces the previous link for this member.
            var code = AccountEndpoints.NewToken();
            var expires = DateTime.UtcNow.Add(LinkLifetime);
            var resetToken = await db.Set<IdentityUserToken<string>>().FirstOrDefaultAsync(t =>
                t.UserId == memberId && t.LoginProvider == Provider &&
                t.Name == LinkName, context.RequestAborted);
            var value = expires.ToString("O") + "|" + AccountEndpoints.Hash(code);
            if (resetToken is null)
                db.Set<IdentityUserToken<string>>().Add(new IdentityUserToken<string>
                {
                    UserId = memberId, LoginProvider = Provider, Name = LinkName,
                    Value = value
                });
            else
                resetToken.Value = value;

            var request = await db.Set<IdentityUserToken<string>>().FirstOrDefaultAsync(t =>
                t.UserId == memberId && t.LoginProvider == Provider &&
                t.Name == RequestName, context.RequestAborted);
            if (request is not null)
                db.Set<IdentityUserToken<string>>().Remove(request);
            await db.SaveChangesAsync(context.RequestAborted);

            var configured = configuration["FRATERNITAS_PUBLIC_URL"];
            var origin = !string.IsNullOrWhiteSpace(configured) ? configured.TrimEnd('/')
                : environment.IsProduction() ? "https://fraternitas.moir.pw"
                : $"{context.Request.Scheme}://{context.Request.Host}";
            var url = $"{origin}/account/reset-password?uid={Uri.EscapeDataString(member.Id)}&code={Uri.EscapeDataString(code)}";
            var safeUrl = WebUtility.HtmlEncode(url);
            var name = WebUtility.HtmlEncode(member.DisplayName);
            NoStore(context);
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(AccountEndpoints.Page("Password reset link", $"""
                <h1>Reset link for {name}</h1>
                <p>Copy this link and send it <strong>privately</strong> to the member.
                   It works once and expires in 2 hours. Creating another link invalidates this one.</p>
                <label>Private reset link
                    <input id="reset-url" readonly value="{safeUrl}" onclick="this.select()">
                </label>
                <p class="muted">The full link is displayed only now; only its hash is saved.
                   Do not post it in the group chat.</p>
                <p><a href="/admin">Return to Administration</a></p>
                """), "text/html");
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        app.MapGet("/account/reset-password", async (
            HttpContext context, IAntiforgery antiforgery, FraternityDb db) =>
        {
            NoStore(context);
            var uid = context.Request.Query["uid"].ToString();
            var code = context.Request.Query["code"].ToString();
            if (!await ValidLinkAsync(db, uid, code))
                return Results.Content(AccountEndpoints.Page("Reset unavailable", """
                    <h1>Reset link unavailable</h1>
                    <p>This password reset link is invalid, expired, or has already been used.
                       Request another link from a group administrator.</p>
                    <p><a href="/account/forgot-password">Request another reset</a></p>
                    """), "text/html");

            var token = WebUtility.HtmlEncode(antiforgery.GetAndStoreTokens(context).RequestToken ?? "");
            var error = context.Request.Query.ContainsKey("error")
                ? "<p class='error' role='alert'>Passwords must match and cannot be empty. Try again.</p>"
                : "";
            return Results.Content(AccountEndpoints.Page("Reset password", $"""
                <h1>Choose a new password</h1>
                <p>Set a new password for your Fraternitas account.</p>{error}
                <form method="post" action="/account/reset-password">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <input type="hidden" name="uid" value="{WebUtility.HtmlEncode(uid)}">
                  <input type="hidden" name="code" value="{WebUtility.HtmlEncode(code)}">
                  <label>New password
                    <input type="password" name="password" autocomplete="new-password" required>
                  </label>
                  <label>Confirm new password
                    <input type="password" name="confirm" autocomplete="new-password" required>
                  </label>
                  <button type="submit">Save new password</button>
                </form>
                """), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/reset-password", async (
            HttpContext context, IAntiforgery antiforgery,
            FraternityDb db, UserManager<Member> users) =>
        {
            NoStore(context);
            if (!await AccountEndpoints.ValidForm(context, antiforgery))
                return Results.BadRequest("Invalid request. Reload your reset link.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var uid = form["uid"].ToString();
            var code = form["code"].ToString();
            if (!await ValidLinkAsync(db, uid, code))
                return Results.Redirect("/account/forgot-password");

            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();
            if (string.IsNullOrEmpty(password) || password != confirm)
                return Results.Redirect($"/account/reset-password?uid={Uri.EscapeDataString(uid)}&code={Uri.EscapeDataString(code)}&error=1");

            var member = await users.FindByIdAsync(uid);
            if (member is null || member.GroupId != 1)
                return Results.Redirect("/account/forgot-password");

            // Use Identity's normal reset operation to update the password hash
            // and security stamp, which invalidates prior sign-in cookies.
            var identityToken = await users.GeneratePasswordResetTokenAsync(member);
            var result = await users.ResetPasswordAsync(member, identityToken, password);
            if (!result.Succeeded)
                return Results.Redirect($"/account/reset-password?uid={Uri.EscapeDataString(uid)}&code={Uri.EscapeDataString(code)}&error=1");

            var saved = await db.Set<IdentityUserToken<string>>().FirstOrDefaultAsync(t =>
                t.UserId == uid && t.LoginProvider == Provider && t.Name == LinkName,
                context.RequestAborted);
            if (saved is not null)
            {
                db.Set<IdentityUserToken<string>>().Remove(saved);
                await db.SaveChangesAsync(context.RequestAborted);
            }
            await users.SetLockoutEndDateAsync(member, null);
            await users.ResetAccessFailedCountAsync(member);
            return Results.Redirect("/account/login?reset=1");
        }).AllowAnonymous();
    }

    private static async Task<bool> ValidLinkAsync(FraternityDb db, string uid, string code)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 128
            || code.Length is < 20 or > 256)
            return false;
        if (!await db.Users.AnyAsync(u => u.Id == uid && u.GroupId == 1))
            return false;
        var token = await db.Set<IdentityUserToken<string>>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == uid && t.LoginProvider == Provider
                && t.Name == LinkName);
        if (token?.Value is not { } value)
            return false;
        var parts = value.Split('|', 2);
        return parts.Length == 2
            && DateTime.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expires)
            && expires > DateTime.UtcNow
            && string.Equals(parts[1], AccountEndpoints.Hash(code), StringComparison.Ordinal);
    }

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
