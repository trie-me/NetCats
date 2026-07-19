using PurrfectSeat.Application;
using PurrfectSeat.Contracts;
using PurrfectSeat.Domain;
using PurrfectSeat.Infrastructure;
using PurrfectSeat.Scenarios;

namespace PurrfectSeat.Scenario.Tests;

public sealed class ScenarioRunnerTests
{
    [Fact]
    public async Task Hot_seats_never_oversells_a_confirmed_seat()
    {
        var repository = new InMemoryPerformanceRepository();
        var performance = new Performance(PerformanceId.New(), "Hot", DateTimeOffset.UtcNow.AddDays(1), [(new SeatId("A01"), new Money(24m, "GBP"))]);
        repository.Seed(performance);
        var application = new BookingApplication(repository, repository, new AlwaysEligibleCustomers(), new FixedPricing(), new SimulatedPayments(), new SystemClock(), new DemoProjection());
        var runner = new ScenarioRunner(application, repository);

        var summary = await runner.RunAsync("hot-seats", new ScenarioRequest(Customers: 20, MaxConcurrency: 8, Seed: 7), CancellationToken.None);

        Assert.True(summary.InvariantsHeld);
        Assert.Equal(1, summary.HoldsCreated);
        Assert.Equal(19, summary.Conflicts);
    }
}
