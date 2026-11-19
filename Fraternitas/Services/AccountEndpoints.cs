using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

public static class AccountEndpoints
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    public static void MapAccountEndpoints(this WebApplication app)
    {
        // First-run setup is deliberately LOCAL and DEVELOPMENT ONLY.
        // A remotely accessible, unauthenticated "claim administrator" page
        // would allow the first visitor to take ownership of a new installation.
        app.MapGet("/account/setup", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, IWebHostEnvironment environment) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!AdminSetup.IsLocalDevelopmentRequest(ctx, environment))
                return Results.NotFound();
            if (await db.Users.AnyAsync())
                return Results.Redirect("/account/login");

            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            var error = ctx.Request.Query.ContainsKey("error")
                ? "<p class='error'>Check your email and make sure both non-empty passwords match. Try again.</p>"
                : "";
            var form = $"""
                <h1>Set up Fraternitas</h1>
                <p>Welcome! Create your administrator account to get started.</p>{error}
                <form method="post" action="/account/setup">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <label>Your name<input name="name" maxlength="100" autocomplete="name" required></label>
                  <label>Email<input name="email" type="email" maxlength="256" autocomplete="username" required></label>
                  <label>Password<input name="password" type="password"
                    autocomplete="new-password" required></label>
                  <label>Confirm password<input name="confirm" type="password"
                    autocomplete="new-password" required></label>
                  <button type="submit">Create administrator</button>
                </form>
                <p class="muted">Only available on localhost in Development mode. No preset password is used.</p>
                """;
            return Results.Content(Page("First-run setup", form), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/setup", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, IWebHostEnvironment environment,
            UserManager<Member> users, SignInManager<Member> signIn) =>
        {
            if (!AdminSetup.IsLocalDevelopmentRequest(ctx, environment))
                return Results.NotFound();
            if (!await ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");

            await SetupGate.WaitAsync();
            try
            {
                // Disable setup forever after the first account exists.
                if (await db.Users.AnyAsync()) return Results.Redirect("/account/login");

                var form = await ctx.Request.ReadFormAsync();
                var email = form["email"].ToString().Trim();
                var name = form["name"].ToString().Trim();
                var password = form["password"].ToString();
                var confirmation = form["confirm"].ToString();
                if (!AdminSetup.ValidEmail(email) || name.Length is < 1 or > 100
                    || string.IsNullOrEmpty(password) || password != confirmation)
                    return Results.Redirect("/account/setup?error=1");

                var member = new Member
                {
                    UserName = email, Email = email, EmailConfirmed = true,
                    DisplayName = name, GroupId = 1
                };
                var result = await users.CreateAsync(member, password);
                if (!result.Succeeded)
                    return Results.Redirect("/account/setup?error=1");

                foreach (var role in new[] { "Admin", "Member" })
                {
                    result = await users.AddToRoleAsync(member, role);
                    if (!result.Succeeded)
                    {
                        await users.DeleteAsync(member);
                        return Results.Problem("Could not finish setting up the administrator.");
                    }
                }
                await signIn.SignInAsync(member, isPersistent: false);
                return Results.Redirect("/");
            }
            finally
            {
                SetupGate.Release();
            }
        }).AllowAnonymous();

        app.MapGet("/account/login", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, IWebHostEnvironment environment) =>
        {
            if (AdminSetup.IsLocalDevelopmentRequest(ctx, environment)
                && !await db.Users.AnyAsync())
                return Results.Redirect("/account/setup");
            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            var returnUrl = WebUtility.HtmlEncode(ctx.Request.Query["returnUrl"].ToString());
            var error = ctx.Request.Query.ContainsKey("error") ? "<p class='error'>Invalid credentials or account temporarily locked.</p>" : "";
            var form = $"""
                <h1>Welcome back</h1><p>Sign in to your Fraternitas group.</p>{error}
                <form method="post" action="/account/login">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <input type="hidden" name="returnUrl" value="{returnUrl}">
                  <label>Email<input type="email" name="email" autocomplete="username" required></label>
                  <label>Password<input type="password" name="password" autocomplete="current-password" required></label>
                  <button type="submit">Sign in</button>
                </form>
                <p class="muted">New here? Ask a group administrator for an invitation link.</p>
                """;
            return Results.Content(Page("Sign in", form), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/login", async (HttpContext ctx, IAntiforgery csrf, SignInManager<Member> signIn) =>
        {
            if (!await ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var result = await signIn.PasswordSignInAsync(email, password, false, lockoutOnFailure: true);
            if (!result.Succeeded) return Results.Redirect("/account/login?error=1");
            var target = form["returnUrl"].ToString();
            if (!target.StartsWith('/') || target.StartsWith("//") || target.StartsWith(@"/\"))
                target = "/";
            return Results.Redirect(target);
        }).AllowAnonymous();

        app.MapGet("/account/join", (HttpContext ctx, IAntiforgery csrf) =>
        {
            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            var code = WebUtility.HtmlEncode(ctx.Request.Query["code"].ToString());
            var error = ctx.Request.Query.ContainsKey("error") ? "<p class='error'>This invitation may have expired or been revoked, or your account details are invalid. Use a unique email and a non-empty password.</p>" : "";
            var form = $"""
                <h1>Join the fraternity</h1><p>Create your private group account.</p>
                <p class="muted">New members receive Administrator access by default. Your group can change your access to Member only later.</p>{error}
                <form method="post" action="/account/join">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <label>Invitation code<input name="code" value="{code}" required></label>
                  <label>Your name<input name="name" maxlength="100" required></label>
                  <label>Email<input type="email" name="email" autocomplete="username" required></label>
                  <label>Password<input type="password" name="password" autocomplete="new-password" required></label>
                  <button type="submit">Create account</button>
                </form>
                """;
            return Results.Content(Page("Join", form), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/join", async (HttpContext ctx, IAntiforgery csrf,
            FraternityDb db, UserManager<Member> users, SignInManager<Member> signIn) =>
        {
            if (!await ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            var form = await ctx.Request.ReadFormAsync();
            var code = form["code"].ToString().Trim();
            var email = form["email"].ToString().Trim();
            var name = form["name"].ToString().Trim();
            var password = form["password"].ToString();

            if (code.Length < 20 || name.Length is < 1 or > 100 || !AdminSetup.ValidEmail(email) || string.IsNullOrEmpty(password))
                return Results.Redirect("/account/join?error=1");

            var hash = Hash(code);
            // A shared invitation remains valid for any number of distinct members until expiry or admin revocation.
            // The existing RedeemedUtc column acts as a deactivation marker, including legacy redeemed links.
            await using var transaction = await db.Database.BeginTransactionAsync();
            var invite = await db.Invitations.FirstOrDefaultAsync(i =>
                i.TokenSha256 == hash && i.RedeemedUtc == null && i.ExpiresUtc > DateTime.UtcNow);
            if (invite is null) return Results.Redirect("/account/join?error=1");

            var member = new Member { UserName = email, Email = email, DisplayName = name,
                GroupId = invite.GroupId, EmailConfirmed = true };
            var created = await users.CreateAsync(member, password);
            if (!created.Succeeded) return Results.Redirect("/account/join?error=1");
            // Everyone joining the group starts as an administrator unless an
            // existing administrator later explicitly sets their access to Member only.
            foreach (var role in new[] { "Member", "Admin" })
            {
                var assigned = await users.AddToRoleAsync(member, role);
                if (!assigned.Succeeded)
                    return Results.Problem("Could not finish creating your account.");
            }
            await transaction.CommitAsync();
            await signIn.SignInAsync(member, false);
            return Results.Redirect("/");
        }).AllowAnonymous();

        app.MapGet("/account/logout", (HttpContext ctx, IAntiforgery csrf) =>
        {
            var token = WebUtility.HtmlEncode(csrf.GetAndStoreTokens(ctx).RequestToken ?? "");
            return Results.Content(Page("Sign out", $"""
                <h1>Sign out?</h1><form method="post" action="/account/logout">
                <input type="hidden" name="__RequestVerificationToken" value="{token}">
                <button type="submit">Sign out</button></form>
                <p><a href="/">Return to the group</a></p>
                """), "text/html");
        }).RequireAuthorization();

        app.MapPost("/account/logout", async (HttpContext ctx, IAntiforgery csrf,
            SignInManager<Member> signIn) =>
        {
            if (!await ValidForm(ctx, csrf)) return Results.BadRequest("Invalid request.");
            await signIn.SignOutAsync();
            return Results.Redirect("/account/login");
        }).RequireAuthorization();
    }

    private static async Task<bool> ValidForm(HttpContext ctx, IAntiforgery csrf)
    {
        try { await csrf.ValidateRequestAsync(ctx); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    public static string NewToken() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Page(string title, string body)
    {
        const string template = """
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>__TITLE__ | Fraternitas</title>
            <style>
              body{font:16px system-ui,sans-serif;background:#f7f4ee;color:#23372f;min-height:100vh;
                display:grid;place-items:center;margin:0;padding:1rem}
              main{max-width:410px;width:100%;padding:2rem;background:white;border:1px solid #e2ded3;
                border-radius:16px;box-shadow:0 10px 35px #0001}
              label{display:block;font-weight:600;margin:1rem 0}input{display:block;box-sizing:border-box;
                width:100%;padding:.85rem;border:1px solid #ccc;border-radius:8px;margin-top:.35rem;font:inherit}
              button{background:#244c40;color:white;border:0;padding:.85rem 1.3rem;border-radius:8px;
                font-weight:700;cursor:pointer;font:inherit;width:100%}a{color:#244c40}
              .muted{color:#69756d;font-size:.85rem}.error{color:#9b2424}
            </style></head><body><main><p style="font-weight:700;letter-spacing:.12em">✝ FRATERNITAS</p>
            __BODY__</main></body></html>
            """;
        return template.Replace("__TITLE__", WebUtility.HtmlEncode(title))
            .Replace("__BODY__", body);
    }
}
