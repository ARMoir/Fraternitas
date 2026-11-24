using System.Net;
using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

public static class AccountRequestEndpoints
{
    public static void MapAccountRequestEndpoints(this WebApplication app)
    {
        app.MapGet("/account/request", (HttpContext ctx, IAntiforgery csrf) =>
        {
            PrivatePage(ctx);
            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            var error = ctx.Request.Query.ContainsKey("error")
                ? "<p class='error' role='alert'>Please check your name and email address.</p>" : "";
            return Html("Request an account", $"""
                <h1>Request an account</h1>
                <p>Interested in joining Fraternitas? Send your details to the group administrators for approval.</p>
                {error}
                <form method="post" action="/account/request">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <label>Your name<input name="name" maxlength="100" autocomplete="name" required></label>
                  <label>Email<input name="email" type="email" maxlength="256" autocomplete="email" required></label>
                  <label>How did you hear about us? (optional)<input name="note" maxlength="500"></label>
                  <button type="submit">Request an account</button>
                </form>
                <p class="muted">If approved, you will receive a private registration link to choose your password.</p>
                <p><a href="/account/login">Back to sign in</a></p>
                """);
        }).AllowAnonymous();

        app.MapPost("/account/request", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, UserManager<Member> users) =>
        {
            PrivatePage(ctx);
            if (!await AccountEndpoints.ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var name = form["name"].ToString().Trim();
            var email = form["email"].ToString().Trim();
            var note = form["note"].ToString().Trim();
            if (name.Length is < 1 or > 100 || !AdminSetup.ValidEmail(email) || note.Length > 500)
                return Results.Redirect("/account/request?error=1");

            var normalized = users.NormalizeEmail(email);
            await using (var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted))
            {
                // Deduplicate requests and bound the public queue. Never send mail
                // or create an Identity account based on an anonymous submission.
                if (!await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ctx.RequestAborted)
                    && !await db.AccountRequests.AnyAsync(r => r.NormalizedEmail == normalized, ctx.RequestAborted)
                    && await db.AccountRequests.CountAsync(ctx.RequestAborted) < 200)
                {
                    db.AccountRequests.Add(new AccountRequest
                    {
                        DisplayName = name, Email = email, NormalizedEmail = normalized!, Note = note
                    });
                    await db.SaveChangesAsync(ctx.RequestAborted);
                }
                await transaction.CommitAsync(ctx.RequestAborted);
            }
            // Same response for new, duplicate, and existing addresses.
            return Html("Account request", """
                <h1>Request received</h1>
                <p>Your request will be reviewed by the group administrators. If approved, you will receive a private registration link.</p>
                <p><a href="/account/login">Return to sign in</a></p>
                """);
        }).AllowAnonymous();

        var admin = app.MapGroup("/admin/account-requests")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        admin.MapPost("/approve", async (HttpContext ctx, IAntiforgery csrf, MemberAccessService access,
            FraternityDb db, FraternitasEmailService mail, IConfiguration config) =>
        {
            PrivatePage(ctx);
            if (!await access.IsAdministratorAsync(ctx.User)) return Results.Forbid();
            if (!await AccountEndpoints.ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (!Guid.TryParse(form["requestId"], out var id)) return Results.Redirect("/admin?status=failed");
            // New accounts always start as members. Promotion is a separate admin action.
            var code = AccountEndpoints.NewToken();
            AccountRequest? request;
            await using (var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted))
            {
                request = await db.AccountRequests.FindAsync(new object[] { id }, ctx.RequestAborted);
                if (request is null || await db.Users.AnyAsync(u =>
                        u.NormalizedEmail == request.NormalizedEmail, ctx.RequestAborted))
                    return Results.Redirect("/admin?status=failed");
                request.TokenSha256 = AccountEndpoints.Hash(code);
                request.ExpiresUtc = DateTime.UtcNow.AddDays(7);
                request.GrantAdmin = false;
                await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ctx.RequestAborted);
            }
            // Email links always use the configured/canonical public hostname.
            var origin = config["FRATERNITAS_PUBLIC_URL"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(origin)) origin = "https://fraternitas.moir.pw";
            var url = origin + "/account/complete-registration?code=" + Uri.EscapeDataString(code);
            var sent = await mail.SendAsync(request.Email, "Fraternitas · Your account request is approved",
                "Welcome to Fraternitas!\n\nYour request has been approved. Complete your registration and choose your password using this private link:\n"
                + url + "\n\nThe link is valid for seven days and can only be used once. Please do not share it.\n\nPax Christi.",
                ctx.RequestAborted);
            var status = sent.Accepted
                ? "<p class='success' role='status'>Resend accepted the registration email.</p>"
                : $"<p class='error' role='alert'>{WebUtility.HtmlEncode(sent.Message)} You can still share the private link directly.</p>";
            var safeUrl = WebUtility.HtmlEncode(url);
            return Html("Account approved", $"""
                <h1>Account request approved</h1>
                <p>{WebUtility.HtmlEncode(request.DisplayName)} · {WebUtility.HtmlEncode(request.Email)}</p>
                {status}
                <label>Private registration link<input id="registration-url" value="{safeUrl}" readonly onclick="this.select()"></label>
                <p class="muted">Valid for seven days, single-use. Resending or deleting this request invalidates this link.</p>
                <p><a href="/admin#account-requests">Return to Administration</a></p>
                """);
        });

        admin.MapPost("/delete", async (HttpContext ctx, IAntiforgery csrf, MemberAccessService access, FraternityDb db) =>
        {
            if (!await access.IsAdministratorAsync(ctx.User)) return Results.Forbid();
            if (!await AccountEndpoints.ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (!Guid.TryParse(form["requestId"], out var id)) return Results.Redirect("/admin?status=failed");
            // Only delete an application, never an active account. Any outstanding
            // private registration link becomes invalid as soon as the row is gone.
            await db.AccountRequests.Where(r => r.Id == id).ExecuteDeleteAsync(ctx.RequestAborted);
            return Results.Redirect("/admin?status=request-deleted#account-requests");
        });

        app.MapGet("/account/complete-registration", async (HttpContext ctx, IAntiforgery csrf, FraternityDb db) =>
        {
            PrivatePage(ctx);
            var code = ctx.Request.Query["code"].ToString();
            var request = await FindApprovedAsync(db, code, ctx.RequestAborted);
            if (request is null) return InvalidLink();
            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            var error = ctx.Request.Query.ContainsKey("error")
                ? "<p class='error' role='alert'>Choose matching passwords with at least 6 characters and one number.</p>" : "";
            return Html("Complete registration", $"""
                <h1>Complete your registration</h1>
                <p>Welcome, {WebUtility.HtmlEncode(request.DisplayName)}. Your account request has been approved.</p>
                {error}
                <form method="post" action="/account/complete-registration">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <input type="hidden" name="code" value="{WebUtility.HtmlEncode(code)}">
                  <label>Your name<input name="name" maxlength="100" value="{WebUtility.HtmlEncode(request.DisplayName)}" required></label>
                  <label>Email<input type="email" value="{WebUtility.HtmlEncode(request.Email)}" readonly></label>
                  <p class="muted small">Use at least 6 characters and one number.</p>
                  <label>Password<input name="password" type="password" autocomplete="new-password" minlength="6" pattern=".*[0-9].*" title="At least 6 characters and one number" required></label>
                  <label>Confirm password<input name="confirm" type="password" autocomplete="new-password" minlength="6" pattern=".*[0-9].*" title="At least 6 characters and one number" required></label>
                  <button type="submit">Create account</button>
                </form>
                """);
        }).AllowAnonymous();

        app.MapPost("/account/complete-registration", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, UserManager<Member> users, SignInManager<Member> signIn) =>
        {
            PrivatePage(ctx);
            if (!await AccountEndpoints.ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var code = form["code"].ToString();
            var name = form["name"].ToString().Trim();
            var password = form["password"].ToString();
            await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            var request = await FindApprovedAsync(db, code, ctx.RequestAborted);
            if (request is null) return InvalidLink();
            if (name.Length is < 1 or > 100 || !PasswordPolicy.IsValid(password) || password != form["confirm"].ToString())
                return Results.Redirect("/account/complete-registration?error=1&code=" + Uri.EscapeDataString(code));
            var member = new Member
            {
                UserName = request.Email, Email = request.Email, EmailConfirmed = true,
                DisplayName = name, GroupId = 1
            };
            if (!(await users.CreateAsync(member, password)).Succeeded)
                return Html("Registration unavailable", """
                    <h1>Registration could not be completed</h1>
                    <p>If you already have an account, sign in. Otherwise contact an administrator.</p>
                    <p><a href="/account/login">Sign in</a></p>
                    """);
            if (!(await users.AddToRoleAsync(member, "Member")).Succeeded)
                return Results.Problem("Could not finish registration.");
            var accessResult = await users.AddClaimAsync(member, new Claim(MemberAccessService.MemberOnlyClaimType, "true"));
            if (!accessResult.Succeeded) return Results.Problem("Could not finish registration.");
            // Consume the approved request atomically with creating the account.
            db.AccountRequests.Remove(request);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await transaction.CommitAsync(ctx.RequestAborted);
            await signIn.SignInAsync(member, false);
            return Results.Redirect("/");
        }).AllowAnonymous();
    }

    private static async Task<AccountRequest?> FindApprovedAsync(FraternityDb db, string code, CancellationToken cancellation)
    {
        if (code.Length is < 20 or > 256) return null;
        var hash = AccountEndpoints.Hash(code);
        return await db.AccountRequests.FirstOrDefaultAsync(r => r.TokenSha256 == hash
            && r.ExpiresUtc > DateTime.UtcNow, cancellation);
    }

    private static IResult InvalidLink() => Html("Registration unavailable", """
        <h1>Registration link unavailable</h1>
        <p>This link has expired, was revoked, or was already used. Contact a group administrator.</p>
        <p><a href="/account/login">Back to sign in</a></p>
        """);

    private static IResult Html(string title, string body) =>
        Results.Content(AccountEndpoints.Page(title, body), "text/html; charset=utf-8");

    private static void PrivatePage(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
