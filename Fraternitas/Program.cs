using System.Text;
using Fraternitas.Components;
using Fraternitas.Data;
using Fraternitas.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

var dataPath = Environment.GetEnvironmentVariable("FRATERNITAS_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataPath))
    dataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
dataPath = Path.GetFullPath(dataPath);
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(Path.Combine(dataPath, "documents"));

builder.Services.AddSingleton(new DataStorage(dataPath));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDbContextFactory<FraternityDb>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataPath, "fraternitas.db")}"));
builder.Services.AddIdentity<Member, IdentityRole>(options =>
{
    options.Password.RequiredLength = 12;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireDigit = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.User.RequireUniqueEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<FraternityDb>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTPS is required for public deployment.
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
});

builder.Services.AddAuthorization();
builder.Services.AddRadzenComponents();

var app = builder.Build();
await DatabaseSetup.InitializeAsync(app.Services, app.Logger);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAccountEndpoints();
app.MapGet("/resources/file/{id:guid}", async (Guid id, IDbContextFactory<FraternityDb> factory,
    DataStorage storage) =>
{
    await using var db = await factory.CreateDbContextAsync();
    var doc = await db.SharedDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.GroupId == 1);
    if (doc is null) return Results.NotFound();
    var fullPath = Path.Combine(storage.DocumentsDirectory, doc.StoredName);
    return File.Exists(fullPath)
        ? Results.File(fullPath, doc.ContentType, doc.FileName)
        : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/events/{id:guid}/calendar.ics", async (Guid id, IDbContextFactory<FraternityDb> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    var ev = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.GroupId == 1);
    if (ev is null) return Results.NotFound();
    string Escape(string value) => value.Replace(@"\", @"\\").Replace(";", @"\;")
        .Replace(",", @"\,").Replace("\r\n", @"\n").Replace("\n", @"\n");
    var start = GroupClock.ToUtc(ev.StartLocal);
    var end = GroupClock.ToUtc(ev.EndLocal);
    var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Fraternitas//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{ev.Id}@fraternitas\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\n"
        + $"DTSTART:{start:yyyyMMddTHHmmssZ}\r\nDTEND:{end:yyyyMMddTHHmmssZ}\r\n"
        + $"SUMMARY:{Escape(ev.Title)}\r\nDESCRIPTION:{Escape(ev.Description)}\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";
    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar", "fraternitas-event.ics");
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
