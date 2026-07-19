using PurrfectSeat.Domain;

namespace PurrfectSeat.Domain.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public void Confirmed_seats_cannot_be_released_and_hydration_preserves_the_aggregate_state()
    {
        var performance = NewPerformance();
        var hold = performance.HoldSeats(HoldId.New(), new CustomerId(Guid.NewGuid()), new HashSet<SeatId> { new("A01") }, new Money(24m, "GBP"), DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow);
        performance.ConfirmHold(hold.Id, PaymentId.New(), "checkout-1", DateTimeOffset.UtcNow);

        Assert.Throws<DomainRuleViolation>(() => performance.CancelHold(hold.Id, DateTimeOffset.UtcNow));
        var hydrated = Performance.Hydrate(performance.ToSnapshot());

        Assert.Equal(SeatState.Confirmed, Assert.Single(hydrated.Seats).State);
        Assert.Equal(HoldStatus.Confirmed, Assert.Single(hydrated.Holds).Status);
        Assert.Empty(hydrated.DequeueEvents());
    }

    [Fact]
    public void A_seat_cannot_belong_to_two_active_holds()
    {
        var performance = NewPerformance();
        performance.HoldSeats(HoldId.New(), new CustomerId(Guid.NewGuid()), new HashSet<SeatId> { new("A01") }, new Money(24m, "GBP"), DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow);

        var error = Assert.Throws<DomainRuleViolation>(() => performance.HoldSeats(HoldId.New(), new CustomerId(Guid.NewGuid()), new HashSet<SeatId> { new("A01") }, new Money(24m, "GBP"), DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow));

        Assert.Equal("seats_unavailable", error.Code);
    }

    private static Performance NewPerformance() => new(
        PerformanceId.New(),
        "Test performance",
        DateTimeOffset.UtcNow.AddDays(1),
        [(new SeatId("A01"), new Money(24m, "GBP"))]);
}
