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
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/account/login", (HttpContext ctx, IAntiforgery csrf) =>
        {
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
            var error = ctx.Request.Query.ContainsKey("error") ? "<p class='error'>This invitation is invalid, expired, already used, or the account details are not valid. Use a unique email and a 12+ character password.</p>" : "";
            var form = $"""
                <h1>Join the fraternity</h1><p>Create your private member account.</p>{error}
                <form method="post" action="/account/join">
                  <input type="hidden" name="__RequestVerificationToken" value="{token}">
                  <label>Invitation code<input name="code" value="{code}" required></label>
                  <label>Your name<input name="name" maxlength="100" required></label>
                  <label>Email<input type="email" name="email" autocomplete="username" required></label>
                  <label>Password (12+ characters)<input type="password" name="password" autocomplete="new-password" minlength="12" required></label>
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

            if (code.Length < 20 || name.Length is < 1 or > 100 || email.Length > 256)
                return Results.Redirect("/account/join?error=1");

            var hash = Hash(code);
            // SQLite serializes writes; the unique email constraint and single-use token guard double registration.
            await using var transaction = await db.Database.BeginTransactionAsync();
            var invite = await db.Invitations.FirstOrDefaultAsync(i =>
                i.TokenSha256 == hash && i.RedeemedUtc == null && i.ExpiresUtc > DateTime.UtcNow);
            if (invite is null) return Results.Redirect("/account/join?error=1");

            var member = new Member { UserName = email, Email = email, DisplayName = name,
                GroupId = invite.GroupId, EmailConfirmed = true };
            var created = await users.CreateAsync(member, password);
            if (!created.Succeeded) return Results.Redirect("/account/join?error=1");
            await users.AddToRoleAsync(member, "Member");
            invite.RedeemedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
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

    private static string Page(string title, string body) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{WebUtility.HtmlEncode(title)} | Fraternitas</title>
        <style>
          body{{font:16px system-ui,sans-serif;background:#f7f4ee;color:#23372f;min-height:100vh;
            display:grid;place-items:center;margin:0;padding:1rem}}
          main{{max-width:410px;width:100%;padding:2rem;background:white;border:1px solid #e2ded3;
            border-radius:16px;box-shadow:0 10px 35px #0001}}
          label{{display:block;font-weight:600;margin:1rem 0}}input{{display:block;box-sizing:border-box;
            width:100%;padding:.85rem;border:1px solid #ccc;border-radius:8px;margin-top:.35rem;font:inherit}}
          button{{background:#244c40;color:white;border:0;padding:.85rem 1.3rem;border-radius:8px;
            font-weight:700;cursor:pointer;font:inherit;width:100%}}a{{color:#244c40}}
          .muted{{color:#69756d;font-size:.85rem}}.error{{color:#9b2424}}
        </style></head><body><main><p style="font-weight:700;letter-spacing:.12em">✝ FRATERNITAS</p>
        {body}</main></body></html>
        """;
}
