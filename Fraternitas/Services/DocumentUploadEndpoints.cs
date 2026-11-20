using System.Security.Claims;
using Fraternitas.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Services;

/// <summary>
/// Secure multipart uploads over ordinary HTTP requests. File selection should
/// never require a live Blazor Server connection to transfer the file.
/// </summary>
public static class DocumentUploadEndpoints
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const long MaxRequestBytes = 12 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".png", ".jpg", ".jpeg" };

    public static void MapDocumentUploadEndpoints(this WebApplication app)
    {
        app.MapPost("/documents/upload", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IDbContextFactory<FraternityDb> dbFactory,
            DataStorage storage,
            ILoggerFactory loggers) =>
        {
            // Multipart bodies are buffered during form parsing. Enforce a
            // request-level limit as well as the per-file limit.
            if (context.Request.ContentLength is > MaxRequestBytes)
                return Results.Redirect("/documents?upload=invalid");

            if (!context.Request.HasFormContentType)
                return Results.BadRequest("Expected a multipart document upload.");

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest("Your session expired. Reload Documents and try again.");
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("document");
            if (file is null || file.Length is <= 0 or > MaxFileBytes)
                return Results.Redirect("/documents?upload=invalid");

            // Never use the untrusted client filename as a filesystem path.
            var originalName = file.FileName.Replace('\\', '/').Split('/').Last();
            var extension = Path.GetExtension(originalName);
            if (!AllowedExtensions.Contains(extension))
                return Results.Redirect("/documents?upload=invalid");
            originalName = originalName.Length > 180 ? originalName[..180] : originalName;

            Guid? eventId = null;
            var selectedEvent = form["eventId"].ToString();
            if (!string.IsNullOrWhiteSpace(selectedEvent))
            {
                if (!Guid.TryParse(selectedEvent, out var parsed))
                    return Results.Redirect("/documents?upload=event");
                eventId = parsed;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            if (eventId.HasValue && !await db.CalendarEvents.AnyAsync(ev =>
                    ev.Id == eventId && ev.GroupId == 1))
                return Results.Redirect("/documents?upload=event");

            var storedName = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
            var filePath = Path.Combine(storage.DocumentsDirectory, storedName);
            var saved = false;
            try
            {
                Directory.CreateDirectory(storage.DocumentsDirectory);
                await using (var output = new FileStream(
                    filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await file.CopyToAsync(output, context.RequestAborted);
                }

                db.SharedDocuments.Add(new SharedDocument
                {
                    FileName = originalName,
                    ContentType = extension.ToLowerInvariant() switch
                    {
                        ".pdf" => "application/pdf",
                        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        ".txt" => "text/plain",
                        ".png" => "image/png",
                        _ => "image/jpeg"
                    },
                    StoredName = storedName,
                    Length = file.Length,
                    UploadedById = userId,
                    EventId = eventId,
                    GroupId = 1
                });
                await db.SaveChangesAsync(context.RequestAborted);
                saved = true;
                return Results.Redirect("/documents?upload=success");
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or DbUpdateException)
            {
                loggers.CreateLogger("DocumentUpload")
                    .LogError(ex, "Member document upload failed");
                return Results.Redirect("/documents?upload=failed");
            }
            finally
            {
                if (!saved && System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }
        })
        .RequireAuthorization()
        .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }
}
