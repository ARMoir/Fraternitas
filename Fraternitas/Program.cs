using System.Text;
using Fraternitas.Components;
using Fraternitas.Data;
using Fraternitas.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dataPath = Environment.GetEnvironmentVariable("FRATERNITAS_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataPath))
{
    // Keep Visual Studio F5 and "dotnet run --project ..." on the SAME database
    // even when those commands start from different working directories.
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    var basePath = File.Exists(Path.Combine(projectRoot, "Fraternitas.csproj"))
        ? projectRoot
        : builder.Environment.ContentRootPath;
    dataPath = Path.Combine(basePath, "App_Data");
}
dataPath = Path.GetFullPath(dataPath);
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(Path.Combine(dataPath, "documents"));

builder.Services.AddSingleton(new DataStorage(dataPath));
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDbContextFactory<FraternityDb>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataPath, "fraternitas.db")}"));
builder.Services.AddIdentity<Member, IdentityRole>(options =>
{
    // New passwords: six characters and at least one digit; existing hashes remain valid.
    options.Password.RequiredLength = PasswordPolicy.MinimumLength;
    options.Password.RequiredUniqueChars = 1;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireDigit = true;
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

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<FraternitasEmailService>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com/");
    client.Timeout = TimeSpan.FromSeconds(12);
});
builder.Services.AddHttpClient<CatholicBibleService>(client =>
{
    client.BaseAddress = new Uri("https://bible.helloao.org/");
    client.Timeout = TimeSpan.FromSeconds(12);
});
builder.Services.AddScoped<ScripturePinService>();
builder.Services.AddScoped<MemberAccessService>();
// User-specific calendar and future UI preferences are stored in Identity's SQLite claims table.
builder.Services.AddScoped<UserPreferencesService>();
// Refresh HTTP authorization cookies after an administrator changes a user's role.
builder.Services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddAuthorization();

// Offline integration test: no real API key or email delivery.
if (args.Contains("verify-password-reset-email", StringComparer.OrdinalIgnoreCase))
{
    await PasswordResetEmailVerifier.VerifyAsync();
    return;
}

var app = builder.Build();
await DatabaseSetup.InitializeAsync(app.Services, app.Logger);
await MemberAccessService.ReconcileDefaultsAsync(app.Services, app.Logger);

// Explicit local recovery command; never binds a web server.
// dotnet run --project Fraternitas/Fraternitas.csproj -- admin-setup
if (args.Contains("admin-setup", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await AdminSetup.RunConsoleSetupAsync(app.Services) ? 0 : 1;
    return;
}

// Integration test: Member defaults, preserved administrators, and explicit role overrides.
// Run with a disposable FRATERNITAS_DATA_DIR.
if (args.Contains("verify-member-access", StringComparer.OrdinalIgnoreCase))
{
    await MemberAccessVerifier.VerifyAsync(app.Services, app.Logger);
    return;
}

// Password-policy regression check with disposable SQLite.
if (args.Contains("verify-password-policy", StringComparer.OrdinalIgnoreCase))
{
    await PasswordPolicyVerifier.VerifyAsync(app.Services);
    return;
}

// Regression verification of per-user calendar preferences with disposable SQLite.
if (args.Contains("verify-user-preferences", StringComparer.OrdinalIgnoreCase))
{
    await UserPreferenceVerifier.VerifyAsync(app.Services);
    return;
}

// Integration test helper; run only with a disposable data directory.
if (args.Contains("verify-scripture-pins", StringComparer.OrdinalIgnoreCase))
{
    await ScripturePinVerifier.VerifyAsync(app.Services);
    return;
}

// Run a real Catholic-canon + passage fetch verification without starting the web server.
// dotnet run --project Fraternitas/Fraternitas.csproj -- verify-scripture
if (args.Contains("verify-scripture", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var bible = scope.ServiceProvider.GetRequiredService<CatholicBibleService>();
    var references = new[]
    {
        "Luke 1:26–38", "Luke 1:39–56", "Luke 2:1–20",
        "Luke 2:22–38", "Luke 2:41–52",
        "JHN 3:16", "TOB 1:1"
    };
    var complete = true;
    foreach (var reference in references)
    {
        var passage = await bible.GetPassageAsync(reference);
        Console.WriteLine($"{reference}: {(passage.IsAvailable ? passage.Verses.Count + " verified Catholic verses" : "FAILED")}");
        if (!passage.IsAvailable)
        {
            complete = false;
            Console.Error.WriteLine(passage.Error);
        }
    }
    try
    {
        var books = await bible.GetBooksAsync();
        var chapter = await bible.GetChapterAsync("JHN", 3);
        var chapterHasVerse = chapter.IsAvailable && chapter.Verses.Any(v => v.Number == 16);
        complete &= books.Count == 73 && books.Any(b => b.Id == "TOB") && chapterHasVerse;
        Console.WriteLine($"Catholic Bible browser: {books.Count} books, John 3 chapter loaded, verse 16 present: {chapterHasVerse}");
    }
    catch (Exception ex)
    {
        complete = false;
        Console.Error.WriteLine("Catholic Bible browser verification failed: " + ex.Message);
    }
    Environment.ExitCode = complete ? 0 : 1;
    return;
}


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
// Opening the site in Visual Studio on a fresh database immediately displays
// the admin setup form. A real, remote web visitor cannot claim an admin account.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method)
        && AdminSetup.IsLocalDevelopmentRequest(context, app.Environment))
    {
        var factory = context.RequestServices.GetRequiredService<IDbContextFactory<FraternityDb>>();
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync())
        {
            context.Response.Redirect("/account/setup");
            return;
        }
    }
    await next();
});

app.UseHttpsRedirection();
// Serve our small site stylesheet directly from disk. This avoids intermittent
// empty HTTP 200 responses from the published static-assets endpoint on the Pi.
// No-store also prevents proxies or browsers from retaining a broken response.
var appCssPath = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "app.css");
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Path.Value, "/app.css", StringComparison.OrdinalIgnoreCase)
        && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
    {
        if (!File.Exists(appCssPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/css; charset=utf-8";
        context.Response.ContentLength = new System.IO.FileInfo(appCssPath).Length;
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        if (HttpMethods.IsGet(context.Request.Method))
            await context.Response.SendFileAsync(appCssPath);
        return;
    }

    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAccountEndpoints();
app.MapAccountRequestEndpoints();
app.MapPasswordRecoveryEndpoints();
app.MapEmailTestEndpoints();
app.MapUserPreferenceEndpoints();
app.MapDocumentUploadEndpoints();
app.MapCalendarEventEndpoints();
app.MapAdminActionEndpoints();
app.MapDiscussionActionEndpoints();
app.MapScripturePinEndpoints();
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
    var allDay = ev.StartLocal.TimeOfDay == TimeSpan.Zero
        && ev.EndLocal == ev.StartLocal.Date.AddDays(1);
    var dates = allDay
        ? $"DTSTART;VALUE=DATE:{ev.StartLocal:yyyyMMdd}\r\nDTEND;VALUE=DATE:{ev.EndLocal:yyyyMMdd}\r\n"
        : $"DTSTART:{GroupClock.ToUtc(ev.StartLocal):yyyyMMddTHHmmssZ}\r\nDTEND:{GroupClock.ToUtc(ev.EndLocal):yyyyMMddTHHmmssZ}\r\n";
    var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Fraternitas//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{ev.Id}@fraternitas\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\n"
        + dates
        + $"SUMMARY:{Escape(ev.Title)}\r\nDESCRIPTION:{Escape(ev.Description)}\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";
    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar", "fraternitas-event.ics");
}).RequireAuthorization();

// Retain built-in ASP.NET static asset mapping for published web resources.
app.MapStaticAssets();
app.MapRazorComponents<App>();
app.Run();
