using System.Collections.Concurrent;
using PurrfectSeat.Application;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class AlwaysEligibleCustomers : ICustomerService
{
    public Task<CustomerEligibility> CheckEligibilityAsync(CustomerId customerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CustomerEligibility(true));
    }
}

public sealed class FixedPricing(decimal pricePerSeat = 24m, string currency = "GBP") : IPricingService
{
    public Task<Money> QuoteAsync(PerformanceId performanceId, IReadOnlyCollection<SeatId> seats, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Money(pricePerSeat * seats.Count, currency));
    }
}

public sealed class SimulatedPayments : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, PaymentAuthorization> successfulAuthorizations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);

    private int voidedCount;

    public int VoidedCount => Volatile.Read(ref voidedCount);

    public async Task<PaymentAuthorization> AuthorizeAsync(string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (successfulAuthorizations.TryGetValue(idempotencyKey, out var existing))
        {
            return existing;
        }

        var attempt = attempts.AddOrUpdate(idempotencyKey, 1, static (_, count) => count + 1);
        switch (paymentMethodToken)
        {
            case "bank-of-meow-decline":
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                throw new PaymentDeclinedException();
            case "hairball-gateway":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("An indefinitely delayed payment should be cancelled by its timeout.");
            case "lazy-tabby":
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                break;
            case "nine-lives-card" when attempt == 1:
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                throw new PaymentTransientException();
            default:
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                break;
        }

        var authorization = new PaymentAuthorization(PaymentId.New(), idempotencyKey);
        return successfulAuthorizations.GetOrAdd(idempotencyKey, authorization);
    }

    public Task VoidAsync(PaymentAuthorization authorization, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        successfulAuthorizations.TryRemove(authorization.IdempotencyKey, out _);
        Interlocked.Increment(ref voidedCount);
        return Task.CompletedTask;
    }
}
