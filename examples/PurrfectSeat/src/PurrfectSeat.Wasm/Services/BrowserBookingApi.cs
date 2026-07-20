using PurrfectSeat.Application;
using PurrfectSeat.Contracts;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Wasm.Services;

/// <summary>
/// An API-shaped bridge over the same application services used by the server host.
/// It lets a static WebAssembly deployment run the demo in the browser without a
/// browser-impossible HTTP listener or a separate backend.
/// </summary>
public sealed class BrowserBookingApi(BookingApplication application, IPerformanceReader performances)
{
    public async Task<IReadOnlyList<PerformanceSummaryResponse>> ListPerformancesAsync(CancellationToken cancellationToken = default)
    {
        var catalogue = await performances.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return catalogue.OrderBy(static performance => performance.StartsAt)
            .Select(static performance => new PerformanceSummaryResponse(performance.Id.Value, performance.Name, performance.StartsAt))
            .ToArray();
    }

    public async Task<BrowserApiResponse<AvailabilityResponse>> GetAvailabilityAsync(Guid performanceId, CancellationToken cancellationToken = default)
    {
        var performance = await application.GetPerformance(new PerformanceId(performanceId)).RunAsync(cancellationToken).ConfigureAwait(false);
        return performance is null
            ? BrowserApiResponse<AvailabilityResponse>.NotFound()
            : BrowserApiResponse<AvailabilityResponse>.Ok(ToAvailability(performance));
    }

    public async Task<BrowserApiResponse<HoldResponse>> PlaceHoldAsync(Guid performanceId, Guid customerId, IReadOnlyCollection<string> seats, CancellationToken cancellationToken = default)
    {
        if (seats.Count == 0 || seats.Any(String.IsNullOrWhiteSpace) || seats.Distinct(StringComparer.OrdinalIgnoreCase).Count() != seats.Count)
        {
            return BrowserApiResponse<HoldResponse>.Validation("Select at least one unique seat.");
        }

        var result = await application.HoldSeats(new HoldSeatsCommand(
            new PerformanceId(performanceId),
            HoldId.New(),
            new CustomerId(customerId),
            new HashSet<SeatId>(seats.Select(static seat => new SeatId(seat.Trim()))))).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            HoldSeatsResult.Created created => BrowserApiResponse<HoldResponse>.Created(ToResponse(created.Hold)),
            HoldSeatsResult.NotFound => BrowserApiResponse<HoldResponse>.NotFound(),
            HoldSeatsResult.Unavailable unavailable => BrowserApiResponse<HoldResponse>.Conflict("seats_unavailable", $"Seats unavailable: {string.Join(',', unavailable.Seats)}."),
            HoldSeatsResult.Invalid invalid => BrowserApiResponse<HoldResponse>.Validation(string.Join(' ', invalid.Errors.SelectMany(static pair => pair.Value))),
            _ => throw new InvalidOperationException("Unknown hold result."),
        };
    }

    public async Task<BrowserApiResponse<HoldResponse>> ConfirmHoldAsync(Guid performanceId, Guid holdId, string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var result = await application.ConfirmHold(new ConfirmHoldCommand(new PerformanceId(performanceId), new HoldId(holdId), paymentMethodToken, idempotencyKey)).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConfirmHoldResult.Confirmed confirmed => BrowserApiResponse<HoldResponse>.Ok(ToResponse(confirmed.Hold)),
            ConfirmHoldResult.NotFound => BrowserApiResponse<HoldResponse>.NotFound(),
            ConfirmHoldResult.Conflict conflict => BrowserApiResponse<HoldResponse>.Conflict(conflict.Code, conflict.Detail),
            ConfirmHoldResult.Declined => BrowserApiResponse<HoldResponse>.Failure(422, "payment_declined", "The simulated payment was declined."),
            ConfirmHoldResult.TimedOut => BrowserApiResponse<HoldResponse>.Failure(504, "payment_timeout", "The payment provider did not respond in time."),
            _ => throw new InvalidOperationException("Unknown confirmation result."),
        };
    }

    public async Task<BrowserApiResponse<object>> CancelHoldAsync(Guid performanceId, Guid holdId, CancellationToken cancellationToken = default)
    {
        var result = await application.CancelHold(new CancelHoldCommand(new PerformanceId(performanceId), new HoldId(holdId))).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            CancelHoldResult.Cancelled => BrowserApiResponse<object>.NoContent(),
            CancelHoldResult.NotFound => BrowserApiResponse<object>.NotFound(),
            CancelHoldResult.Conflict conflict => BrowserApiResponse<object>.Conflict(conflict.Code, conflict.Detail),
            _ => throw new InvalidOperationException("Unknown cancellation result."),
        };
    }

    private static HoldResponse ToResponse(HoldSnapshot hold) => new(
        hold.Id.Value, hold.CustomerId.Value, hold.Seats.Select(static seat => seat.Value).ToArray(), hold.Price.Amount,
        hold.Price.Currency, hold.ExpiresAt, hold.Status.ToString(), hold.PaymentId?.Value);

    private static AvailabilityResponse ToAvailability(Performance performance)
    {
        var expiryByHold = performance.Holds.ToDictionary(static hold => hold.Id, static hold => hold.ExpiresAt);
        return new AvailabilityResponse(performance.Id.Value, performance.Name, performance.StartsAt, performance.Version.Value,
            performance.Seats.Select(seat => new SeatResponse(seat.Id.Value, seat.State.ToString(), seat.HoldId is { } holdId && expiryByHold.TryGetValue(holdId, out var expiry) ? expiry : null, seat.Price.Amount, seat.Price.Currency)).ToArray());
    }
}

public sealed record BrowserApiResponse<T>(int StatusCode, T? Value, string? Code, string? Message)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public static BrowserApiResponse<T> Ok(T value) => new(200, value, null, null);
    public static BrowserApiResponse<T> Created(T value) => new(201, value, null, null);
    public static BrowserApiResponse<T> NoContent() => new(204, default, null, null);
    public static BrowserApiResponse<T> NotFound() => new(404, default, "not_found", "The requested performance or hold does not exist.");
    public static BrowserApiResponse<T> Validation(string message) => new(400, default, "validation", message);
    public static BrowserApiResponse<T> Conflict(string code, string message) => new(409, default, code, message);
    public static BrowserApiResponse<T> Failure(int statusCode, string code, string message) => new(statusCode, default, code, message);
}
