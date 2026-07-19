using PurrfectSeat.Application;
using PurrfectSeat.Domain;
using PurrfectSeat.Infrastructure;

namespace PurrfectSeat.Application.Tests;

public sealed class BookingApplicationTests
{
    [Fact]
    public async Task Hold_workflow_is_cold_then_commits_a_hydrated_aggregate()
    {
        var (application, repository, performance, _) = CreateSystem();
        var effect = application.HoldSeats(new HoldSeatsCommand(performance.Id, HoldId.New(), new CustomerId(Guid.NewGuid()), new HashSet<SeatId> { new("A01") }));

        Assert.Empty((await repository.LoadAllAsync(CancellationToken.None)).Single().Holds);
        var result = await effect.RunAsync();

        Assert.IsType<HoldSeatsResult.Created>(result);
        var reloaded = (await repository.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Equal(HoldStatus.Active, Assert.Single(reloaded.Holds).Status);
        Assert.Equal(SeatState.Held, Assert.Single(reloaded.Seats).State);
    }

    [Fact]
    public async Task Declined_payment_keeps_hold_active_and_does_not_confirm_the_seat()
    {
        var (application, repository, performance, _) = CreateSystem();
        var created = Assert.IsType<HoldSeatsResult.Created>(await application.HoldSeats(new HoldSeatsCommand(performance.Id, HoldId.New(), new CustomerId(Guid.NewGuid()), new HashSet<SeatId> { new("A01") })).RunAsync());

        var result = await application.ConfirmHold(new ConfirmHoldCommand(performance.Id, created.Hold.Id, "bank-of-meow-decline", "declined-key")).RunAsync();

        Assert.IsType<ConfirmHoldResult.Declined>(result);
        var reloaded = (await repository.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Equal(HoldStatus.Active, Assert.Single(reloaded.Holds).Status);
    }

    [Fact]
    public async Task A_hold_is_isolated_to_its_selected_showtime()
    {
        var repository = new InMemoryPerformanceRepository();
        var catalogue = SeedData.CreateCatalogue();
        foreach (var performance in catalogue)
        {
            repository.Seed(performance);
        }

        var application = new BookingApplication(repository, repository, new AlwaysEligibleCustomers(), new FixedPricing(), new SimulatedPayments(), new SystemClock(), new DemoProjection());
        var selectedShowtime = catalogue[1];
        var result = await application.HoldSeats(new HoldSeatsCommand(
            selectedShowtime.Id,
            HoldId.New(),
            new CustomerId(Guid.NewGuid()),
            new HashSet<SeatId> { new("A01") })).RunAsync();

        Assert.IsType<HoldSeatsResult.Created>(result);
        var reloaded = await repository.LoadAllAsync(CancellationToken.None);
        Assert.Equal(SeatState.Held, reloaded.Single(performance => performance.Id == selectedShowtime.Id).Seats.Single(seat => seat.Id == new SeatId("A01")).State);
        Assert.All(reloaded.Where(performance => performance.Id != selectedShowtime.Id), performance =>
            Assert.Equal(SeatState.Available, performance.Seats.Single(seat => seat.Id == new SeatId("A01")).State));
    }

    private static (BookingApplication Application, InMemoryPerformanceRepository Repository, Performance Performance, SimulatedPayments Payments) CreateSystem()
    {
        var repository = new InMemoryPerformanceRepository();
        var performance = new Performance(PerformanceId.New(), "Test", DateTimeOffset.UtcNow.AddDays(1), [(new SeatId("A01"), new Money(24m, "GBP"))]);
        repository.Seed(performance);
        var payments = new SimulatedPayments();
        var application = new BookingApplication(repository, repository, new AlwaysEligibleCustomers(), new FixedPricing(), payments, new SystemClock(), new DemoProjection());
        return (application, repository, performance, payments);
    }
}
