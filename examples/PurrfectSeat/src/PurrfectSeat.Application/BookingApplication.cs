using NetCats.Core;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Application;

public sealed record BookingOptions(TimeSpan HoldDuration, TimeSpan PaymentTimeout, int OptimisticRetryLimit = 3)
{
    public static BookingOptions Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2));
}

public sealed class BookingApplication(
    IBookingUnitOfWorkFactory unitOfWorkFactory,
    IPerformanceReader performanceReader,
    ICustomerService customers,
    IPricingService pricing,
    IPaymentGateway payments,
    IClock clock,
    IApplicationEventSink events,
    BookingOptions? options = null)
{
    private readonly BookingOptions options = options ?? BookingOptions.Default;

    public Latent<HoldSeatsResult> HoldSeats(HoldSeatsCommand command) =>
        Latent<HoldSeatsResult>.DelayAsync(token => HoldSeatsAsync(command, token));

    public Latent<ConfirmHoldResult> ConfirmHold(ConfirmHoldCommand command) =>
        Latent<ConfirmHoldResult>.DelayAsync(token => ConfirmHoldAsync(command, token));

    public Latent<CancelHoldResult> CancelHold(CancelHoldCommand command) =>
        Latent<CancelHoldResult>.DelayAsync(token => CancelHoldAsync(command, token));

    public Latent<Performance?> GetPerformance(PerformanceId performanceId) =>
        Latent<Performance?>.DelayAsync(async token =>
        {
            await using var unitOfWork = await unitOfWorkFactory.OpenAsync(token).ConfigureAwait(false);
            return await unitOfWork.LoadAsync(performanceId, token).ConfigureAwait(false);
        });

    public Latent<HoldSnapshot?> GetHold(PerformanceId performanceId, HoldId holdId) =>
        GetPerformance(performanceId).Map(performance => performance?.FindHold(holdId));

    public Latent<int> ExpireDueHolds() => Latent<int>.DelayAsync(ExpireDueHoldsAsync);

    private async Task<HoldSeatsResult> HoldSeatsAsync(HoldSeatsCommand command, CancellationToken token)
    {
        if (command.Seats.Count == 0)
        {
            return Invalid("seats", "Select at least one seat.");
        }

        var duplicate = command.Seats.GroupBy(static seat => seat).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            return Invalid("seats", "Seat identifiers must be unique.");
        }

        var eligibility = customers.CheckEligibilityAsync(command.CustomerId, token);
        var quote = pricing.QuoteAsync(command.PerformanceId, command.Seats.ToArray(), token);
        await Task.WhenAll(eligibility, quote).ConfigureAwait(false);
        if (!eligibility.Result.IsEligible)
        {
            return Invalid("customer", eligibility.Result.Reason ?? "Customer is not eligible.");
        }

        for (var attempt = 0; attempt <= options.OptimisticRetryLimit; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await using var unitOfWork = await unitOfWorkFactory.OpenAsync(token).ConfigureAwait(false);
            var performance = await unitOfWork.LoadAsync(command.PerformanceId, token).ConfigureAwait(false);
            if (performance is null)
            {
                return new HoldSeatsResult.NotFound();
            }

            var expectedVersion = performance.Version;
            HoldSnapshot hold;
            try
            {
                hold = performance.HoldSeats(command.HoldId, command.CustomerId, command.Seats, await quote.ConfigureAwait(false), clock.UtcNow + options.HoldDuration, clock.UtcNow);
            }
            catch (DomainRuleViolation error) when (error.Code == "seats_unavailable")
            {
                return new HoldSeatsResult.Unavailable(command.Seats.ToArray());
            }
            catch (DomainRuleViolation error)
            {
                return Invalid("seats", error.Message);
            }

            var pendingEvents = performance.DequeueEvents();
            var saved = await unitOfWork.SaveAsync(performance, expectedVersion, pendingEvents, token).ConfigureAwait(false);
            if (!saved.Saved)
            {
                continue;
            }

            if (!(await unitOfWork.CommitAsync(token).ConfigureAwait(false)).Saved)
            {
                continue;
            }

            await events.PublishAsync(pendingEvents, token).ConfigureAwait(false);
            return new HoldSeatsResult.Created(hold);
        }

        return new HoldSeatsResult.Unavailable(command.Seats.ToArray());
    }

    private async Task<ConfirmHoldResult> ConfirmHoldAsync(ConfirmHoldCommand command, CancellationToken token)
    {
        PaymentAuthorization? authorization = null;
        var committed = false;
        try
        {
            authorization = await AuthorizeWithBoundedRetryAsync(command, token).ConfigureAwait(false);
            for (var attempt = 0; attempt <= options.OptimisticRetryLimit; attempt++)
            {
                await using var unitOfWork = await unitOfWorkFactory.OpenAsync(token).ConfigureAwait(false);
                var performance = await unitOfWork.LoadAsync(command.PerformanceId, token).ConfigureAwait(false);
                if (performance is null || performance.FindHold(command.HoldId) is null)
                {
                    return new ConfirmHoldResult.NotFound();
                }

                var expectedVersion = performance.Version;
                HoldSnapshot hold;
                try
                {
                    hold = performance.ConfirmHold(command.HoldId, authorization.PaymentId, command.IdempotencyKey, clock.UtcNow);
                }
                catch (DomainRuleViolation error)
                {
                    return new ConfirmHoldResult.Conflict(error.Code, error.Message);
                }

                var pendingEvents = performance.DequeueEvents();
                var saved = await unitOfWork.SaveAsync(performance, expectedVersion, pendingEvents, token).ConfigureAwait(false);
                if (!saved.Saved)
                {
                    continue;
                }

                if (!(await unitOfWork.CommitAsync(token).ConfigureAwait(false)).Saved)
                {
                    continue;
                }

                await events.PublishAsync(pendingEvents, token).ConfigureAwait(false);
                committed = true;
                return new ConfirmHoldResult.Confirmed(hold);
            }

            return new ConfirmHoldResult.Conflict("confirmation_conflict", "The reservation changed while checkout was completing.");
        }
        catch (PaymentDeclinedException)
        {
            return new ConfirmHoldResult.Declined();
        }
        catch (PaymentTimedOutException)
        {
            return new ConfirmHoldResult.TimedOut();
        }
        finally
        {
            if (authorization is not null && !committed)
            {
                await payments.VoidAsync(authorization, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<CancelHoldResult> CancelHoldAsync(CancelHoldCommand command, CancellationToken token)
    {
        await using var unitOfWork = await unitOfWorkFactory.OpenAsync(token).ConfigureAwait(false);
        var performance = await unitOfWork.LoadAsync(command.PerformanceId, token).ConfigureAwait(false);
        if (performance is null || performance.FindHold(command.HoldId) is null)
        {
            return new CancelHoldResult.NotFound();
        }

        var expectedVersion = performance.Version;
        try
        {
            performance.CancelHold(command.HoldId, clock.UtcNow);
        }
        catch (DomainRuleViolation error)
        {
            return new CancelHoldResult.Conflict(error.Code, error.Message);
        }

        var pendingEvents = performance.DequeueEvents();
        var saved = await unitOfWork.SaveAsync(performance, expectedVersion, pendingEvents, token).ConfigureAwait(false);
        if (!saved.Saved)
        {
            return new CancelHoldResult.Conflict("confirmation_conflict", "The reservation changed while cancellation was completing.");
        }

        if (!(await unitOfWork.CommitAsync(token).ConfigureAwait(false)).Saved)
        {
            return new CancelHoldResult.Conflict("confirmation_conflict", "The reservation changed while cancellation was completing.");
        }

        await events.PublishAsync(pendingEvents, token).ConfigureAwait(false);
        return new CancelHoldResult.Cancelled();
    }

    private async Task<int> ExpireDueHoldsAsync(CancellationToken token)
    {
        var expired = 0;
        var performances = await performanceReader.LoadAllAsync(token).ConfigureAwait(false);
        foreach (var current in performances)
        {
            foreach (var active in current.Holds.Where(static hold => hold.Status == HoldStatus.Active && hold.ExpiresAt <= DateTimeOffset.MaxValue).ToArray())
            {
                if (active.ExpiresAt > clock.UtcNow)
                {
                    continue;
                }

                await using var unitOfWork = await unitOfWorkFactory.OpenAsync(token).ConfigureAwait(false);
                var performance = await unitOfWork.LoadAsync(current.Id, token).ConfigureAwait(false);
                if (performance is null)
                {
                    continue;
                }

                var expectedVersion = performance.Version;
                if (performance.ExpireHold(active.Id, clock.UtcNow) is null)
                {
                    continue;
                }

                var pendingEvents = performance.DequeueEvents();
                if (!(await unitOfWork.SaveAsync(performance, expectedVersion, pendingEvents, token).ConfigureAwait(false)).Saved)
                {
                    continue;
                }

                if (!(await unitOfWork.CommitAsync(token).ConfigureAwait(false)).Saved)
                {
                    continue;
                }

                await events.PublishAsync(pendingEvents, token).ConfigureAwait(false);
                expired++;
            }
        }

        return expired;
    }

    private async Task<PaymentAuthorization> AuthorizeWithBoundedRetryAsync(ConfirmHoldCommand command, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.PaymentTimeout);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await payments.AuthorizeAsync(command.PaymentMethodToken, command.IdempotencyKey, timeout.Token).ConfigureAwait(false);
            }
            catch (PaymentTransientException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new PaymentTimedOutException();
            }
        }
    }

    private static HoldSeatsResult.Invalid Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
