using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Data;

public sealed class FraternityDb(DbContextOptions<FraternityDb> options) : IdentityDbContext<Member>(options)
{
    public DbSet<FraternityGroup> Groups => Set<FraternityGroup>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<Discussion> Discussions => Set<Discussion>();
    public DbSet<Reply> Replies => Set<Reply>();
    public DbSet<SharedDocument> SharedDocuments => Set<SharedDocument>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AccountRequest> AccountRequests => Set<AccountRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<CalendarEvent>().HasIndex(e => new { e.GroupId, e.StartLocal });
        builder.Entity<Discussion>().HasIndex(e => new { e.GroupId, e.CreatedUtc });
        builder.Entity<Invitation>().HasIndex(e => e.TokenSha256).IsUnique();
        builder.Entity<AccountRequest>().HasIndex(e => e.NormalizedEmail).IsUnique();
        builder.Entity<AccountRequest>().HasIndex(e => e.TokenSha256).IsUnique();
        builder.Entity<Reply>().HasOne(r => r.Discussion).WithMany(d => d.Replies)
            .HasForeignKey(r => r.DiscussionId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Discussion>().HasOne(d => d.Author).WithMany().HasForeignKey(d => d.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Reply>().HasOne(r => r.Author).WithMany().HasForeignKey(r => r.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public static class DatabaseSetup
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FraternityDb>();
        await db.Database.EnsureCreatedAsync();
        // EnsureCreated does not add tables to existing SQLite databases.
        // This additive, idempotent upgrade preserves all existing group data.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AccountRequests" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "DisplayName" TEXT NOT NULL,
                "Email" TEXT NOT NULL,
                "NormalizedEmail" TEXT NOT NULL,
                "Note" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "TokenSha256" TEXT NULL,
                "ExpiresUtc" TEXT NULL,
                "GrantAdmin" INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccountRequests_NormalizedEmail"
                ON "AccountRequests" ("NormalizedEmail");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccountRequests_TokenSha256"
                ON "AccountRequests" ("TokenSha256");
            """);

        // Existing calendars have no recorded owner. Add a nullable column before any event query.
        var ownerColumnCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('CalendarEvents') WHERE name = 'CreatedByUserId'").SingleAsync();
        if (ownerColumnCount == 0)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE CalendarEvents ADD COLUMN CreatedByUserId TEXT NULL");

        if (!await db.Groups.AnyAsync())
        {
            db.Groups.Add(new FraternityGroup { Id = 1, Name = "Men of Advent",
                Description = "Prayer, penance, and fraternity in Christ." });
        }
        else
        {
            // Only update the original, unedited starter description.
            var originalGroup = await db.Groups.FirstOrDefaultAsync(g => g.Id == 1);
            if (originalGroup?.Description == "Prayer, asceticism, and fraternity.")
                originalGroup.Description = "Prayer, penance, and fraternity in Christ.";
        }

        if (!await db.Readings.AnyAsync())
        {
            db.Readings.AddRange(
                new Reading { Week = 1, Title = "The Annunciation", Scripture = "Luke 1:26–38",
                    Summary = "Mary's fiat and openness to God's will." },
                new Reading { Week = 2, Title = "The Visitation", Scripture = "Luke 1:39–56",
                    Summary = "Mary visits Elizabeth in faith and charity." },
                new Reading { Week = 3, Title = "The Nativity", Scripture = "Luke 2:1–20",
                    Summary = "The birth of Jesus Christ in Bethlehem." },
                new Reading { Week = 4, Title = "The Presentation", Scripture = "Luke 2:22–38",
                    Summary = "Mary and Joseph present Jesus in the Temple." },
                new Reading { Week = 5, Title = "The Finding in the Temple", Scripture = "Luke 2:41–52",
                    Summary = "Jesus is found among the teachers in Jerusalem." }
            );
        }

        await db.SaveChangesAsync();
        await AdventCalendarSeeder.SeedAsync(db, logger);

        var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
        foreach (var role in new[] { "Admin", "Member" })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(role));

        // Admin creation is deliberately interactive. Never seed passwords or accounts
        // from environment variables on ordinary application startup.
    }
}
