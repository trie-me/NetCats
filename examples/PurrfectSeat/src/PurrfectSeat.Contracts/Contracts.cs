namespace PurrfectSeat.Contracts;

public sealed record HoldSeatsRequest(Guid CustomerId, IReadOnlyList<string> Seats);

public sealed record ConfirmHoldRequest(string PaymentMethodToken, string IdempotencyKey);

public sealed record HoldResponse(
    Guid Id,
    Guid CustomerId,
    IReadOnlyList<string> Seats,
    decimal Price,
    string Currency,
    DateTimeOffset ExpiresAt,
    string Status,
    Guid? PaymentId);

public sealed record SeatResponse(string Id, string State, DateTimeOffset? HoldExpiresAt, decimal Price, string Currency);

public sealed record AvailabilityResponse(Guid PerformanceId, string Name, DateTimeOffset StartsAt, int Version, IReadOnlyList<SeatResponse> Seats);

public sealed record PerformanceSummaryResponse(Guid Id, string Name, DateTimeOffset StartsAt);

public sealed record AvailabilityEvent(long Id, Guid PerformanceId, int Version, string Kind, DateTimeOffset OccurredAt);

public sealed record TelemetryEvent(long Id, string Category, string Message, DateTimeOffset OccurredAt);

public sealed record TelemetrySnapshot(
    long Cursor,
    int ActiveFibers,
    int FinalizersRunning,
    int QueueDepth,
    int HoldsCreated,
    int HoldsConfirmed,
    int HoldsExpired,
    int SeatConflicts,
    IReadOnlyList<TelemetryEvent> Feed);

public sealed record ScenarioRequest(int Customers = 8, int MaxConcurrency = 4, int Seed = 42, Guid? PerformanceId = null);

public sealed record ScenarioSummary(string Name, int Requested, int HoldsCreated, int Confirmed, int Conflicts, bool InvariantsHeld);
