using PurrfectSeat.Domain;

namespace PurrfectSeat.Infrastructure;

public static class SeedData
{
    public static IReadOnlyList<Performance> CreateCatalogue()
    {
        var friday = NextShowtime(DayOfWeek.Friday, 19, 30);
        var saturdayMatinee = NextShowtime(DayOfWeek.Saturday, 14, 0);
        var saturdayEvening = NextShowtime(DayOfWeek.Saturday, 19, 30);
        return
        [
            CreatePerformance("Midnight at the Apollo", friday),
            CreatePerformance("Midnight at the Apollo", saturdayMatinee),
            CreatePerformance("Midnight at the Apollo", saturdayEvening),
            CreatePerformance("The Meow-sic of the Night", NextShowtime(DayOfWeek.Sunday, 18, 0)),
        ];
    }

    public static Performance CreateMidnightAtTheApollo() => CreateCatalogue()[0];

    private static Performance CreatePerformance(string name, DateTimeOffset startsAt)
    {
        var seats = Enumerable.Range(1, 20)
            .Select(number => (new SeatId($"A{number:D2}"), new Money(24m, "GBP")))
            .Concat(Enumerable.Range(1, 20).Select(number => (new SeatId($"B{number:D2}"), new Money(24m, "GBP"))));
        return new Performance(
            PerformanceId.New(),
            name,
            startsAt,
            seats);
    }

    private static DateTimeOffset NextShowtime(DayOfWeek day, int hour, int minute)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = ((int)day - (int)date.DayOfWeek + 7) % 7;
        if (days == 0 && TimeOnly.FromDateTime(DateTime.UtcNow) >= new TimeOnly(hour, minute))
        {
            days = 7;
        }

        var local = date.AddDays(days).ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);
        return new DateTimeOffset(local);
    }
}
