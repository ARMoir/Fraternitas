using Microsoft.EntityFrameworkCore;

namespace Fraternitas.Data;

/// <summary>
/// One-time 2026 Advent calendar starter based on the group's November meeting notes.
/// Runs for both brand-new and already-existing SQLite databases, without changing
/// the EF schema, duplicating entries or restoring events an admin deliberately deleted.
/// </summary>
public static class AdventCalendarSeeder
{
    private const string SeedKey = "advent-2026-calendar-v1";
    private const string FastingUpdateKey = "advent-2026-fasting-until-4pm-v2";

    private static string FastingTitle(bool wednesday) =>
        wednesday ? "Wednesday fast until 4 PM + meatless" : "Friday fast until 4 PM";

    private static string FastingDescription(bool wednesday) =>
        (wednesday ? "Wednesday fast (meatless).\n\n" : "Friday fast.\n\n") +
        "Fast until 4:00 PM Eastern. As an alternative, have two small meals and one normal meal. " +
        "Approach the commitment prayerfully and consider your family obligations. " +
        "This is a day-long observance with a 4 PM fasting cutoff, not a midnight appointment.";

    private static string OldFastingDescription(bool wednesday) =>
        (wednesday ? "Fasting and meatless Wednesday.\n\n" : "Friday fasting.\n\n") +
        "Choose either two small meals and one normal meal, or fast until 4 PM. " +
        "Approach the commitment prayerfully and consider your family obligations. " +
        "This is an all-day observance, not a fixed appointment.";

    private static readonly DateTime FirstMonday = new(2026, 11, 23);
    private static readonly DateTime LastAdventDay = new(2026, 12, 24);

    public static async Task SeedAsync(FraternityDb db, ILogger logger)
    {
        // This separate history table can safely be added to existing EF EnsureCreated databases.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AppSeedHistory" (
                "Key" TEXT NOT NULL PRIMARY KEY,
                "AppliedUtc" TEXT NOT NULL
            );
            """);

        await using var transaction = await db.Database.BeginTransactionAsync();
        // The marker and the events are committed atomically: no half-seeded calendar.
        var applied = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO "AppSeedHistory" ("Key", "AppliedUtc")
            VALUES ({SeedKey}, {DateTime.UtcNow.ToString("O")});
            """);
        if (applied == 0)
        {
            await transaction.CommitAsync();
            await UpgradeExistingFastsAsync(db, logger);
            return;
        }

        var existing = await db.CalendarEvents.Where(e => e.GroupId == 1
                && e.StartLocal >= FirstMonday && e.StartLocal < LastAdventDay.AddDays(1))
            .Select(e => new { e.StartLocal, e.Category })
            .ToListAsync();
        var occupied = existing
            .Select(e => (e.StartLocal, e.Category))
            .ToHashSet();

        var mysteries = await db.Readings.OrderBy(r => r.Week).Take(5).ToListAsync();
        var seed = new List<CalendarEvent>();

        void AddIfNotAlreadyScheduled(CalendarEvent ev)
        {
            // Respect manually entered events at the same time and of the same category.
            if (occupied.Add((ev.StartLocal, ev.Category)))
                seed.Add(ev);
        }

        // Five Monday 8 PM meetings, November 23 through December 21, 2026.
        for (var week = 0; week < 5; week++)
        {
            var day = FirstMonday.AddDays(7 * week);
            var mystery = mysteries.FirstOrDefault(r => r.Week == week + 1);
            if (mystery is null)
                throw new InvalidOperationException($"Reading guide for Joyful Mystery week {week + 1} is missing.");

            AddIfNotAlreadyScheduled(new CalendarEvent
            {
                GroupId = 1,
                Title = $"Week {week + 1} · {mystery.Title}",
                Category = "Meeting",
                StartLocal = day.AddHours(20),
                EndLocal = day.AddHours(21),
                ReadingId = mystery.Id,
                Scripture = mystery.Scripture,
                Description = $"""
                    Men's Advent gathering · Week {week + 1} of 5

                    Scripture: {mystery.Scripture}
                    Joyful Mystery: {mystery.Title}

                    Lectio Divina:
                    1. Read the scripture: what stands out at the surface?
                    2. Read again: what deeper meaning do you notice?
                    3. Read a third time: how does this connect to your own life this Advent?

                    Share reflections and check in on prayer, asceticism, and fraternity.
                    Close with one decade of the Rosary.

                    Meeting: Monday 8:00–9:00 PM Eastern, on Zoom.
                    The recurring Zoom link will be added when available.
                    """ + (week == 4
                        ? "\n\nChristmas-season men's get-together: discuss plans; date and location TBD."
                        : "")
            });
        }

        // Wednesday (also meatless) and Friday fasting during the group's
        // Advent practices, through Christmas Eve. Christmas Day is not added.
        for (var day = FirstMonday.Date; day <= LastAdventDay; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Friday)
            {
                var isWednesday = day.DayOfWeek == DayOfWeek.Wednesday;
                AddIfNotAlreadyScheduled(new CalendarEvent
                {
                    GroupId = 1,
                    Title = FastingTitle(isWednesday),
                    Category = "Fasting",
                    StartLocal = day,
                    EndLocal = day.AddDays(1),
                    Description = FastingDescription(isWednesday)
                });
            }

            // Personal practice reminders are all-day, not fictitious scheduled
            // 8 AM/9 PM appointments. The calendar hides these by default to avoid clutter.
            AddIfNotAlreadyScheduled(new CalendarEvent
            {
                GroupId = 1,
                Title = "Daily prayer & Examen",
                Category = "Prayer",
                StartLocal = day,
                EndLocal = day.AddDays(1),
                Description = """
                    Spend at least 20 minutes in silent prayer, prioritizing quiet rather than filling the time with reading or the Rosary.

                    Practice the daily Ignatian Examen: reflect on where you recognized God working in your day, where you felt distant, and how you responded.

                    Daily digital asceticism: turn off your phone whenever it isn't required for work or ministry. Giving up TV instead is an acceptable alternative agreed by the group.

                    Choose your own time for these personal practices. No compliance or completion tracking.
                    """
            });
        }

        db.CalendarEvents.AddRange(seed);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await UpgradeExistingFastsAsync(db, logger);
        logger.LogInformation("Seeded {Count} Advent 2026 calendar entries (5 meetings, Wednesday/Friday fasting, daily prayer/Examen), without overwriting existing events.", seed.Count);
    }
    private static async Task UpgradeExistingFastsAsync(FraternityDb db, ILogger logger)
    {
        // Safe for existing SQLite installations: no table/column migration.
        // This one-time upgrade only changes untouched, autogenerated entries,
        // never an administrator's edited title, description, date, or time.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var applied = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO "AppSeedHistory" ("Key", "AppliedUtc")
            VALUES ({FastingUpdateKey}, {DateTime.UtcNow.ToString("O")});
            """);
        if (applied == 0)
        {
            await transaction.CommitAsync();
            return;
        }

        var entries = await db.CalendarEvents
            .Where(e => e.GroupId == 1 && e.Category == "Fasting"
                && e.StartLocal >= FirstMonday && e.StartLocal < LastAdventDay.AddDays(1))
            .ToListAsync();

        var changed = 0;
        foreach (var ev in entries)
        {
            var day = ev.StartLocal.DayOfWeek;
            if (day is not (DayOfWeek.Wednesday or DayOfWeek.Friday)
                || ev.StartLocal.TimeOfDay != TimeSpan.Zero
                || ev.EndLocal != ev.StartLocal.Date.AddDays(1))
                continue;

            var wednesday = day == DayOfWeek.Wednesday;
            var originalTitle = wednesday ? "Wednesday fast + meatless" : "Friday fasting";
            if (ev.Title != originalTitle || ev.Description != OldFastingDescription(wednesday))
                continue;

            ev.Title = FastingTitle(wednesday);
            ev.Description = FastingDescription(wednesday);
            changed++;
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        logger.LogInformation("Updated {Count} unmodified Advent fasting observances to show the 4 PM cutoff.", changed);
    }

}
