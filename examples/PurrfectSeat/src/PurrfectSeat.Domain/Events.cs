namespace PurrfectSeat.Domain;

public abstract record DomainEvent(Guid EventId, PerformanceId PerformanceId, DateTimeOffset OccurredAt);

public sealed record SeatsHeld(
    Guid EventId,
    PerformanceId PerformanceId,
    HoldId HoldId,
    IReadOnlyCollection<SeatId> Seats,
    DateTimeOffset ExpiresAt,
    DateTimeOffset OccurredAt) : DomainEvent(EventId, PerformanceId, OccurredAt);

public sealed record HoldConfirmed(
    Guid EventId,
    PerformanceId PerformanceId,
    HoldId HoldId,
    PaymentId PaymentId,
    DateTimeOffset OccurredAt) : DomainEvent(EventId, PerformanceId, OccurredAt);

public sealed record HoldCancelled(
    Guid EventId,
    PerformanceId PerformanceId,
    HoldId HoldId,
    DateTimeOffset OccurredAt) : DomainEvent(EventId, PerformanceId, OccurredAt);

public sealed record HoldExpired(
    Guid EventId,
    PerformanceId PerformanceId,
    HoldId HoldId,
    DateTimeOffset OccurredAt) : DomainEvent(EventId, PerformanceId, OccurredAt);
