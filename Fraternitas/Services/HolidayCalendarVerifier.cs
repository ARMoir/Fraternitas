using System.Text.Json;
namespace Fraternitas.Services;
public static class HolidayCalendarVerifier
{
    public static void Verify()
    {
        static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Holiday calendar regression."); }
        var us = HolidayCalendarService.UnitedStates(2026);
        Require(us.Any(d => d.Date == new DateTime(2026,11,26) && d.Name == "Thanksgiving Day"));
        Require(us.Any(d => d.Date == new DateTime(2026,7,3) && d.Name == "Independence Day (observed)"));
        Require(us.Any(d => d.Date == new DateTime(2026,7,4) && d.Name == "Independence Day"));
        Require(HolidayCalendarService.UnitedStates(2021).Any(d => d.Date == new DateTime(2021,12,31) && d.Name == "New Year's Day (observed)"));
        Require(!HolidayCalendarService.UnitedStates(2020).Any(d => d.Name.StartsWith("Juneteenth")));
        using var json = JsonDocument.Parse("""
            {"litcal":[
              {"name":"Immaculate Conception","grade":6,"date":"2026-12-08T00:00:00+00:00","holy_day_of_obligation":true},
              {"name":"Saint Lucy","grade":3,"date":"2026-12-13T00:00:00+00:00"},
              {"name":"Weekday","grade":0,"date":"2026-12-09T00:00:00+00:00"},
              {"name":"Vigil Mass","grade":6,"date":"2026-12-07T00:00:00+00:00","is_vigil_mass":true},
              {"name":"Sunday in Ordinary Time","grade":5,"date":"2026-06-07T00:00:00+00:00"}
            ]}
            """);
        var catholic = HolidayCalendarService.ParseCatholic(json.RootElement,2026);
        Require(catholic.Count == 2 && catholic[0].Date == new DateTime(2026,12,8) && catholic[0].Obligation);
        Console.WriteLine("US holidays, observed dates and Catholic rank/date/vigil filtering verified.");
    }
}
