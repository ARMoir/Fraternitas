using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Fraternitas.Services;

public sealed record CalendarObservance(DateTime Date, string Name, string Kind, string Rank, bool Obligation = false);
public sealed record CatholicCalendarResult(IReadOnlyList<CalendarObservance> Days, bool Available, bool Stale = false);

public sealed class HolidayCalendarService(IHttpClientFactory clients, IMemoryCache cache, ILogger<HolidayCalendarService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static IReadOnlyList<CalendarObservance> UnitedStates(int year)
    {
        var days = new List<CalendarObservance>();
        void Add(DateTime date, string name) => days.Add(new(date, name, "us", "US federal holiday"));
        void Fixed(int month, int day, string name)
        {
            var actual = new DateTime(year, month, day);
            Add(actual, name);
            if (actual.DayOfWeek == DayOfWeek.Saturday) Add(actual.AddDays(-1), name + " (observed)");
            if (actual.DayOfWeek == DayOfWeek.Sunday) Add(actual.AddDays(1), name + " (observed)");
        }
        DateTime Nth(int month, DayOfWeek weekday, int n)
        {
            var first = new DateTime(year, month, 1);
            return first.AddDays(((int)weekday - (int)first.DayOfWeek + 7) % 7 + (n - 1) * 7);
        }
        Fixed(1, 1, "New Year's Day");
        Add(Nth(1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
        Add(Nth(2, DayOfWeek.Monday, 3), "Washington's Birthday (Presidents' Day)");
        var may = new DateTime(year, 5, 31);
        Add(may.AddDays(-(((int)may.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7)), "Memorial Day");
        if (year >= 2021) Fixed(6, 19, "Juneteenth");
        Fixed(7, 4, "Independence Day");
        Add(Nth(9, DayOfWeek.Monday, 1), "Labor Day");
        Add(Nth(10, DayOfWeek.Monday, 2), "Columbus Day");
        Fixed(11, 11, "Veterans Day");
        Add(Nth(11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
        Fixed(12, 25, "Christmas Day");
        if (new DateTime(year + 1, 1, 1).DayOfWeek == DayOfWeek.Saturday)
            Add(new DateTime(year, 12, 31), "New Year's Day (observed)");
        return days.Where(d => d.Date.Year == year).OrderBy(d => d.Date).ToArray();
    }

    public async Task<CatholicCalendarResult> CatholicAsync(int year)
    {
        var key = $"litcal-us-{year}";
        if (cache.TryGetValue<CatholicCalendarResult>(key, out var fresh) && fresh is not null) return fresh;
        await gate.WaitAsync();
        try
        {
            if (cache.TryGetValue<CatholicCalendarResult>(key, out fresh) && fresh is not null) return fresh;
            try
            {
                using var response = await clients.CreateClient("LiturgicalCalendar").GetAsync($"calendar/nation/US/{year}?year_type=CIVIL");
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var days = ParseCatholic(json.RootElement, year);
                if (days.Count < 20) throw new InvalidDataException("Incomplete calendar.");
                var result = new CatholicCalendarResult(days, true);
                cache.Set(key + "-last", result, TimeSpan.FromDays(30));
                cache.Set(key, result, TimeSpan.FromHours(24));
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or InvalidOperationException)
            {
                logger.LogWarning("Catholic calendar {Year} unavailable: {Reason}", year, ex.GetType().Name);
                var result = cache.TryGetValue<CatholicCalendarResult>(key + "-last", out var last) && last is not null
                    ? last with { Stale = true } : new CatholicCalendarResult([], false);
                cache.Set(key, result, TimeSpan.FromMinutes(10));
                return result;
            }
        }
        finally { gate.Release(); }
    }

    public static IReadOnlyList<CalendarObservance> ParseCatholic(JsonElement root, int year)
    {
        if (!root.TryGetProperty("litcal", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing litcal array.");
        var result = new List<CalendarObservance>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("is_vigil_mass", out var vigil) && vigil.ValueKind == JsonValueKind.True) continue;
            if (!item.TryGetProperty("grade", out var rank) || rank.ValueKind != JsonValueKind.Number
                || !rank.TryGetInt32(out var grade) || grade is < 1 or > 7) continue;
            if (!item.TryGetProperty("name", out var title) || title.ValueKind != JsonValueKind.String
                || title.GetString() is not { Length: > 0 and <= 500 } name) continue;
            if (name.Contains("Sunday in Ordinary Time", StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("date", out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { Length: >= 10 } dateText
                || !DateTime.TryParseExact(dateText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date.Year != year) continue;
            var label = grade switch { 1 => "Commemoration", 2 => "Optional memorial", 3 => "Memorial", 4 => "Feast", 5 => "Feast of the Lord", 6 => "Solemnity", _ => "Principal celebration" };
            if (item.TryGetProperty("grade_display", out var display) && display.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(display.GetString())) label = display.GetString()!;
            var holy = item.TryGetProperty("holy_day_of_obligation", out var obligation) && obligation.ValueKind == JsonValueKind.True;
            result.Add(new(date, name, "catholic", label, holy));
        }
        return result.Distinct().OrderBy(d => d.Date).ThenBy(d => d.Name).ToArray();
    }
}
