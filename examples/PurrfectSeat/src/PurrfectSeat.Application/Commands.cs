using PurrfectSeat.Domain;

namespace PurrfectSeat.Application;

public sealed record HoldSeatsCommand(
    PerformanceId PerformanceId,
    HoldId HoldId,
    CustomerId CustomerId,
    IReadOnlySet<SeatId> Seats);

public sealed record ConfirmHoldCommand(
    PerformanceId PerformanceId,
    HoldId HoldId,
    string PaymentMethodToken,
    string IdempotencyKey);

public sealed record CancelHoldCommand(PerformanceId PerformanceId, HoldId HoldId);

public abstract record HoldSeatsResult
{
    public sealed record Created(HoldSnapshot Hold) : HoldSeatsResult;
    public sealed record NotFound : HoldSeatsResult;
    public sealed record Unavailable(IReadOnlyCollection<SeatId> Seats) : HoldSeatsResult;
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : HoldSeatsResult;
}

public abstract record ConfirmHoldResult
{
    public sealed record Confirmed(HoldSnapshot Hold) : ConfirmHoldResult;
    public sealed record NotFound : ConfirmHoldResult;
    public sealed record Conflict(string Code, string Detail) : ConfirmHoldResult;
    public sealed record Declined : ConfirmHoldResult;
    public sealed record TimedOut : ConfirmHoldResult;
}

public abstract record CancelHoldResult
{
    public sealed record Cancelled : CancelHoldResult;
    public sealed record NotFound : CancelHoldResult;
    public sealed record Conflict(string Code, string Detail) : CancelHoldResult;
}
