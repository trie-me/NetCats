namespace PurrfectSeat.Domain;

public enum SeatState
{
    Available,
    Held,
    Confirmed,
}

public enum HoldStatus
{
    Active,
    Confirmed,
    Cancelled,
    Expired,
}

public sealed record SeatSnapshot(SeatId Id, SeatState State, HoldId? HoldId, Money Price);

public sealed record HoldSnapshot(
    HoldId Id,
    CustomerId CustomerId,
    IReadOnlyCollection<SeatId> Seats,
    Money Price,
    DateTimeOffset ExpiresAt,
    HoldStatus Status,
    PaymentId? PaymentId,
    string? IdempotencyKey);

/// <summary>
/// A persistence-neutral memento. Repositories store this data and hydrate a new aggregate instance;
/// it is not an event stream or an ORM entity.
/// </summary>
public sealed record PerformanceSnapshot(
    PerformanceId Id,
    string Name,
    DateTimeOffset StartsAt,
    PerformanceVersion Version,
    IReadOnlyCollection<SeatSnapshot> Seats,
    IReadOnlyCollection<HoldSnapshot> Holds);

public sealed class Performance
{
    private readonly Dictionary<SeatId, SeatAllocation> seats;
    private readonly Dictionary<HoldId, Hold> holds;
    private readonly List<DomainEvent> uncommittedEvents = [];

    public Performance(PerformanceId id, string name, DateTimeOffset startsAt, IEnumerable<(SeatId Id, Money Price)> seats)
        : this(id, name, startsAt, PerformanceVersion.Initial,
            seats.ToDictionary(static seat => seat.Id, static seat => new SeatAllocation(seat.Id, seat.Price)),
            [])
    {
    }

    private Performance(
        PerformanceId id,
        string name,
        DateTimeOffset startsAt,
        PerformanceVersion version,
        Dictionary<SeatId, SeatAllocation> seats,
        Dictionary<HoldId, Hold> holds)
    {
        Id = id;
        Name = name;
        StartsAt = startsAt;
        Version = version;
        this.seats = seats;
        this.holds = holds;
    }

    public PerformanceId Id { get; }

    public string Name { get; }

    public DateTimeOffset StartsAt { get; }

    public PerformanceVersion Version { get; private set; }

    public IReadOnlyCollection<SeatSnapshot> Seats => seats.Values
        .OrderBy(static seat => seat.Id.Value, StringComparer.Ordinal)
        .Select(static seat => seat.Snapshot())
        .ToArray();

    public IReadOnlyCollection<HoldSnapshot> Holds => holds.Values
        .OrderBy(static hold => hold.Id.Value)
        .Select(static hold => hold.Snapshot())
        .ToArray();

    public IReadOnlyCollection<DomainEvent> DequeueEvents()
    {
        var events = uncommittedEvents.ToArray();
        uncommittedEvents.Clear();
        return events;
    }

    public PerformanceSnapshot ToSnapshot() => new(Id, Name, StartsAt, Version, Seats, Holds);

    public static Performance Hydrate(PerformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var hydratedSeats = snapshot.Seats.ToDictionary(static seat => seat.Id, SeatAllocation.FromSnapshot);
        var hydratedHolds = snapshot.Holds.ToDictionary(static hold => hold.Id, Hold.FromSnapshot);
        return new Performance(snapshot.Id, snapshot.Name, snapshot.StartsAt, snapshot.Version, hydratedSeats, hydratedHolds);
    }

    public HoldSnapshot HoldSeats(
        HoldId holdId,
        CustomerId customerId,
        IReadOnlySet<SeatId> requestedSeats,
        Money price,
        DateTimeOffset expiresAt,
        DateTimeOffset observedAt)
    {
        if (requestedSeats.Count == 0)
        {
            throw new DomainRuleViolation("empty_seats", "A hold must contain at least one seat.");
        }

        if (holds.ContainsKey(holdId))
        {
            throw new DomainRuleViolation("hold_exists", "The hold already exists.");
        }

        var unavailable = requestedSeats.Where(seat => !seats.TryGetValue(seat, out var allocation) || allocation.State != SeatState.Available).ToArray();
        if (unavailable.Length != 0)
        {
            throw new DomainRuleViolation("seats_unavailable", $"Seats are unavailable: {string.Join(',', unavailable)}.");
        }

        var hold = new Hold(holdId, customerId, requestedSeats.ToArray(), price, expiresAt);
        holds.Add(holdId, hold);
        foreach (var seatId in requestedSeats)
        {
            seats[seatId].Assign(holdId, SeatState.Held);
        }

        Advance(new SeatsHeld(Guid.NewGuid(), Id, holdId, requestedSeats.ToArray(), expiresAt, observedAt));
        return hold.Snapshot();
    }

    public HoldSnapshot ConfirmHold(HoldId holdId, PaymentId paymentId, string idempotencyKey, DateTimeOffset confirmedAt)
    {
        var hold = GetHold(holdId);
        if (hold.Status == HoldStatus.Confirmed)
        {
            if (StringComparer.Ordinal.Equals(hold.IdempotencyKey, idempotencyKey))
            {
                return hold.Snapshot();
            }

            throw new DomainRuleViolation("confirmation_conflict", "The hold was confirmed with a different idempotency key.");
        }

        if (hold.Status != HoldStatus.Active)
        {
            throw new DomainRuleViolation(hold.Status == HoldStatus.Expired ? "hold_expired" : "hold_not_active", "The hold can no longer be confirmed.");
        }

        if (hold.ExpiresAt <= confirmedAt)
        {
            ExpireHold(holdId, confirmedAt);
            throw new DomainRuleViolation("hold_expired", "The hold expired before payment completed.");
        }

        hold.Confirm(paymentId, idempotencyKey);
        foreach (var seatId in hold.Seats)
        {
            seats[seatId].Assign(holdId, SeatState.Confirmed);
        }

        Advance(new HoldConfirmed(Guid.NewGuid(), Id, holdId, paymentId, confirmedAt));
        return hold.Snapshot();
    }

    public HoldSnapshot CancelHold(HoldId holdId, DateTimeOffset cancelledAt)
    {
        var hold = GetHold(holdId);
        if (hold.Status == HoldStatus.Confirmed)
        {
            throw new DomainRuleViolation("hold_confirmed", "A confirmed hold cannot be cancelled.");
        }

        if (hold.Status != HoldStatus.Active)
        {
            throw new DomainRuleViolation("hold_not_active", "The hold is no longer active.");
        }

        TransitionToReleased(hold, HoldStatus.Cancelled);
        Advance(new HoldCancelled(Guid.NewGuid(), Id, holdId, cancelledAt));
        return hold.Snapshot();
    }

    public HoldSnapshot? ExpireHold(HoldId holdId, DateTimeOffset observedAt)
    {
        if (!holds.TryGetValue(holdId, out var hold) || hold.Status != HoldStatus.Active || hold.ExpiresAt > observedAt)
        {
            return null;
        }

        TransitionToReleased(hold, HoldStatus.Expired);
        Advance(new HoldExpired(Guid.NewGuid(), Id, holdId, observedAt));
        return hold.Snapshot();
    }

    public HoldSnapshot? FindHold(HoldId holdId) => holds.TryGetValue(holdId, out var hold) ? hold.Snapshot() : null;

    public Performance Clone() => Hydrate(ToSnapshot());

    private Hold GetHold(HoldId holdId) => holds.TryGetValue(holdId, out var hold)
        ? hold
        : throw new DomainRuleViolation("hold_not_found", "The hold does not exist.");

    private void TransitionToReleased(Hold hold, HoldStatus status)
    {
        hold.Release(status);
        foreach (var seatId in hold.Seats)
        {
            seats[seatId].Assign(null, SeatState.Available);
        }
    }

    private void Advance(DomainEvent domainEvent)
    {
        Version = Version.Next();
        uncommittedEvents.Add(domainEvent);
    }

    private sealed class SeatAllocation(SeatId id, Money price)
    {
        public SeatId Id { get; } = id;
        public Money Price { get; } = price;
        public SeatState State { get; private set; }
        public HoldId? HoldId { get; private set; }

        public void Assign(HoldId? holdId, SeatState state)
        {
            HoldId = holdId;
            State = state;
        }

        public SeatSnapshot Snapshot() => new(Id, State, HoldId, Price);

        public SeatAllocation Clone()
        {
            var copy = new SeatAllocation(Id, Price);
            copy.Assign(HoldId, State);
            return copy;
        }

        public static SeatAllocation FromSnapshot(SeatSnapshot snapshot)
        {
            var allocation = new SeatAllocation(snapshot.Id, snapshot.Price);
            allocation.Assign(snapshot.HoldId, snapshot.State);
            return allocation;
        }
    }

    private sealed class Hold(HoldId id, CustomerId customerId, IReadOnlyCollection<SeatId> seats, Money price, DateTimeOffset expiresAt)
    {
        public HoldId Id { get; } = id;
        public CustomerId CustomerId { get; } = customerId;
        public IReadOnlyCollection<SeatId> Seats { get; } = seats;
        public Money Price { get; } = price;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public HoldStatus Status { get; private set; } = HoldStatus.Active;
        public PaymentId? PaymentId { get; private set; }
        public string? IdempotencyKey { get; private set; }

        public void Confirm(PaymentId paymentId, string idempotencyKey)
        {
            Status = HoldStatus.Confirmed;
            PaymentId = paymentId;
            IdempotencyKey = idempotencyKey;
        }

        public void Release(HoldStatus status) => Status = status;

        public HoldSnapshot Snapshot() => new(Id, CustomerId, Seats, Price, ExpiresAt, Status, PaymentId, IdempotencyKey);

        public Hold Clone()
        {
            var copy = new Hold(Id, CustomerId, Seats.ToArray(), Price, ExpiresAt);
            if (Status == HoldStatus.Confirmed)
            {
                copy.Confirm(PaymentId!.Value, IdempotencyKey!);
            }
            else if (Status != HoldStatus.Active)
            {
                copy.Release(Status);
            }

            return copy;
        }

        public static Hold FromSnapshot(HoldSnapshot snapshot)
        {
            var hold = new Hold(snapshot.Id, snapshot.CustomerId, snapshot.Seats.ToArray(), snapshot.Price, snapshot.ExpiresAt);
            if (snapshot.Status == HoldStatus.Confirmed)
            {
                if (snapshot.PaymentId is null || String.IsNullOrWhiteSpace(snapshot.IdempotencyKey))
                {
                    throw new DomainRuleViolation("invalid_snapshot", "A confirmed hold requires payment details.");
                }

                hold.Confirm(snapshot.PaymentId.Value, snapshot.IdempotencyKey);
            }
            else if (snapshot.Status != HoldStatus.Active)
            {
                hold.Release(snapshot.Status);
            }

            return hold;
        }
    }
}
