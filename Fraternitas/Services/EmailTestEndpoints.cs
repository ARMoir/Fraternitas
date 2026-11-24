using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

namespace Fraternitas.Services;

/// <summary>A protected, explicit test-send action for administrators.</summary>
public static class EmailTestEndpoints
{
    public static void MapEmailTestEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/email/test", async (
            HttpContext context, IAntiforgery csrf,
            MemberAccessService access, FraternitasEmailService email) =>
        {
            if (!await access.IsAdministratorAsync(context.User))
                return Results.Forbid();
            if (!await AccountEndpoints.ValidForm(context, csrf))
                return Results.BadRequest("Invalid form token. Reload Administration.");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var recipient = form["recipient"].ToString().Trim();
            if (recipient.Length is < 3 or > 254)
                return Results.Redirect("/admin?status=email-invalid");

            // Sending only occurs after an authenticated administrator clicks
            // the button. Never send automatically on a page GET.
            var result = await email.SendAsync(recipient, "Fraternitas · Resend test",
                "Ave Maria!\n\nThis is a test email from Fraternitas, hosted on the Raspberry Pi.\n"
                + "If you received this message, outbound email is working.\n\nPax Christi.",
                context.RequestAborted);

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var detail = WebUtility.HtmlEncode(result.Message);
            var id = result.MessageId is null ? ""
                : $"<p class='muted'>Resend message ID: {WebUtility.HtmlEncode(result.MessageId)}</p>";
            var heading = result.Accepted ? "Test email submitted" : "Test email not sent";
            var message = result.Accepted
                ? $"<p class='success'>{detail}</p><p>Recipient: {WebUtility.HtmlEncode(recipient)}. Check your inbox and spam folder. Resend acceptance does not guarantee delivery.</p>"
                : $"<p class='error' role='alert'>{detail}</p>";
            return Results.Content(AccountEndpoints.Page("Email test", $"""
                <h1>{heading}</h1>
                {message}
                {id}
                <p><a href="/admin">Return to Administration</a></p>
                """), "text/html; charset=utf-8");
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
    }
}
