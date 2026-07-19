using PurrfectSeat.Domain;

namespace PurrfectSeat.Application;

public interface IBookingUnitOfWorkFactory
{
    ValueTask<IBookingUnitOfWork> OpenAsync(CancellationToken cancellationToken);
}

public interface IBookingUnitOfWork : IAsyncDisposable
{
    Task<Performance?> LoadAsync(PerformanceId id, CancellationToken cancellationToken);

    Task<SaveAttempt> SaveAsync(
        Performance performance,
        PerformanceVersion expectedVersion,
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken);

    Task<SaveAttempt> CommitAsync(CancellationToken cancellationToken);
}

public sealed record SaveAttempt(bool Saved)
{
    public static SaveAttempt Conflict { get; } = new(false);
    public static SaveAttempt Success { get; } = new(true);
}

public interface IPerformanceReader
{
    Task<IReadOnlyCollection<Performance>> LoadAllAsync(CancellationToken cancellationToken);
}

public interface ICustomerService
{
    Task<CustomerEligibility> CheckEligibilityAsync(CustomerId customerId, CancellationToken cancellationToken);
}

public sealed record CustomerEligibility(bool IsEligible, string? Reason = null);

public interface IPricingService
{
    Task<Money> QuoteAsync(PerformanceId performanceId, IReadOnlyCollection<SeatId> seats, CancellationToken cancellationToken);
}

public interface IPaymentGateway
{
    Task<PaymentAuthorization> AuthorizeAsync(string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken);

    Task VoidAsync(PaymentAuthorization authorization, CancellationToken cancellationToken);
}

public sealed record PaymentAuthorization(PaymentId PaymentId, string IdempotencyKey);

public sealed class PaymentDeclinedException : Exception
{
    public PaymentDeclinedException() : base("The simulated payment was declined.") { }
}

public sealed class PaymentTimedOutException : TimeoutException
{
    public PaymentTimedOutException() : base("The simulated payment provider timed out.") { }
}

public sealed class PaymentTransientException : Exception
{
    public PaymentTransientException() : base("The simulated payment provider had a transient failure.") { }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IApplicationEventSink
{
    Task PublishAsync(IReadOnlyCollection<DomainEvent> events, CancellationToken cancellationToken);
}
