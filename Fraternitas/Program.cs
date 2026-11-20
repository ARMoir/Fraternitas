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
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDbContextFactory<FraternityDb>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataPath, "fraternitas.db")}"));
builder.Services.AddIdentity<Member, IdentityRole>(options =>
{
    // Allow any non-empty password; no complexity or minimum-length rules.
    options.Password.RequiredLength = 1;
    options.Password.RequiredUniqueChars = 1;
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

builder.Services.AddMemoryCache();
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
builder.Services.AddRadzenComponents();

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

// Integration test: default Admin access and persistent explicit Member-only overrides.
// Run with a disposable FRATERNITAS_DATA_DIR.
if (args.Contains("verify-member-access", StringComparer.OrdinalIgnoreCase))
{
    await MemberAccessVerifier.VerifyAsync(app.Services, app.Logger);
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
        "Luke 2:22–38", "Luke 2:41–52"
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
// In Production, the published NuGet static assets are physically present under
// wwwroot/_content/Radzen.Blazor. Serve them directly from disk *before*
// MapStaticAssets: the Pi/Cloudflare combination has intermittently delivered
// empty 200 bodies for the manifest-managed compressed variants. Serving
// these assets without precompressed negotiation also covers Radzen's fonts
// (otherwise Material icon ligatures appear as literal words).
// In Development the directory may not exist; regular static web assets remain
// available through the existing middleware and MapStaticAssets below.
var radzenAssetsDirectory = Path.Combine(
    app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
    "_content", "Radzen.Blazor");
if (Directory.Exists(radzenAssetsDirectory))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(radzenAssetsDirectory),
        RequestPath = "/_content/Radzen.Blazor",
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, max-age=0";
            ctx.Context.Response.Headers.Pragma = "no-cache";
        }
    });
}
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

// .NET 10 serves referenced package static web assets (Radzen CSS/fonts/scripts)
// through MapStaticAssets, including Release builds.
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
