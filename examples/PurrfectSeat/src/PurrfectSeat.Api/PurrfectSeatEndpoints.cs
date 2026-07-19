using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PurrfectSeat.Application;
using PurrfectSeat.Contracts;
using PurrfectSeat.Domain;
using PurrfectSeat.Infrastructure;
using PurrfectSeat.Scenarios;

namespace PurrfectSeat.Api;

public static class PurrfectSeatEndpoints
{
    public static async Task<Ok<IReadOnlyList<PerformanceSummaryResponse>>> ListPerformances(
        IPerformanceReader reader,
        CancellationToken cancellationToken)
    {
        var performances = await reader.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<PerformanceSummaryResponse>>(performances
            .OrderBy(static performance => performance.StartsAt)
            .Select(static performance => new PerformanceSummaryResponse(performance.Id.Value, performance.Name, performance.StartsAt))
            .ToArray());
    }

    public static async Task<Results<Created<HoldResponse>, NotFound, Conflict<ProblemDetails>, ValidationProblem>> HoldSeats(
        Guid performanceId,
        HoldSeatsRequest request,
        BookingApplication application,
        CancellationToken cancellationToken)
    {
        var seatValues = request.Seats.Select(static seat => seat.Trim()).ToArray();
        if (seatValues.Length == 0 || seatValues.Any(String.IsNullOrWhiteSpace) || seatValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() != seatValues.Length)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["seats"] = ["Provide at least one unique seat identifier."] });
        }

        var result = await application.HoldSeats(new HoldSeatsCommand(
            new PerformanceId(performanceId),
            HoldId.New(),
            new CustomerId(request.CustomerId),
            new HashSet<SeatId>(seatValues.Select(static value => new SeatId(value)), SeatIdComparer.OrdinalIgnoreCase))).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            HoldSeatsResult.Created created => TypedResults.Created($"/performances/{performanceId}/holds/{created.Hold.Id.Value}", ToResponse(created.Hold)),
            HoldSeatsResult.NotFound => TypedResults.NotFound(),
            HoldSeatsResult.Unavailable unavailable => TypedResults.Conflict(Problem("seats_unavailable", $"Seats unavailable: {string.Join(',', unavailable.Seats)}.")),
            HoldSeatsResult.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => throw new InvalidOperationException("Unknown hold result."),
        };
    }

    public static async Task<Results<Ok<HoldResponse>, NotFound, Conflict<ProblemDetails>, UnprocessableEntity<ProblemDetails>, ProblemHttpResult>> ConfirmHold(
        Guid performanceId,
        Guid holdId,
        ConfirmHoldRequest request,
        BookingApplication application,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(request.PaymentMethodToken) || String.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return TypedResults.Problem("Payment method and idempotency key are required.", statusCode: StatusCodes.Status400BadRequest, extensions: Code("invalid_payment_request"));
        }

        var result = await application.ConfirmHold(new ConfirmHoldCommand(
            new PerformanceId(performanceId),
            new HoldId(holdId),
            request.PaymentMethodToken,
            request.IdempotencyKey)).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConfirmHoldResult.Confirmed confirmed => TypedResults.Ok(ToResponse(confirmed.Hold)),
            ConfirmHoldResult.NotFound => TypedResults.NotFound(),
            ConfirmHoldResult.Conflict conflict => TypedResults.Conflict(Problem(conflict.Code, conflict.Detail)),
            ConfirmHoldResult.Declined => TypedResults.UnprocessableEntity(Problem("payment_declined", "The simulated payment was declined.")),
            ConfirmHoldResult.TimedOut => TypedResults.Problem("The payment provider did not respond in time.", statusCode: StatusCodes.Status504GatewayTimeout, extensions: Code("payment_timeout")),
            _ => throw new InvalidOperationException("Unknown confirmation result."),
        };
    }

    public static async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> CancelHold(
        Guid performanceId,
        Guid holdId,
        BookingApplication application,
        CancellationToken cancellationToken)
    {
        var result = await application.CancelHold(new CancelHoldCommand(new PerformanceId(performanceId), new HoldId(holdId))).RunAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            CancelHoldResult.Cancelled => TypedResults.NoContent(),
            CancelHoldResult.NotFound => TypedResults.NotFound(),
            CancelHoldResult.Conflict conflict => TypedResults.Conflict(Problem(conflict.Code, conflict.Detail)),
            _ => throw new InvalidOperationException("Unknown cancellation result."),
        };
    }

    public static async Task<Results<Ok<HoldResponse>, NotFound>> GetHold(
        Guid performanceId,
        Guid holdId,
        BookingApplication application,
        CancellationToken cancellationToken)
    {
        var result = await application.GetHold(new PerformanceId(performanceId), new HoldId(holdId)).RunAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(result));
    }

    public static async Task<Results<Ok<AvailabilityResponse>, NotFound>> GetAvailability(
        Guid performanceId,
        BookingApplication application,
        CancellationToken cancellationToken)
    {
        var performance = await application.GetPerformance(new PerformanceId(performanceId)).RunAsync(cancellationToken).ConfigureAwait(false);
        return performance is null ? TypedResults.NotFound() : TypedResults.Ok(ToAvailability(performance));
    }

    public static IResult StreamAvailability(DemoProjection projection, HttpContext context) =>
        TypedResults.ServerSentEvents(projection.AvailabilityStream(context.RequestAborted), eventType: "availability");

    public static Ok<IReadOnlyList<string>> ListScenarios(ScenarioRunner runner) => TypedResults.Ok(runner.Names);

    public static async Task<Results<Ok<ScenarioSummary>, ValidationProblem>> StartScenario(
        string name,
        ScenarioRequest request,
        ScenarioRunner runner,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await runner.RunAsync(name, request, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException error)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["scenario"] = [error.Message] });
        }
    }

    public static Ok<TelemetrySnapshot> TelemetrySnapshot(DemoProjection projection) => TypedResults.Ok(projection.Snapshot());

    public static IResult StreamTelemetry(DemoProjection projection, HttpContext context) =>
        TypedResults.ServerSentEvents(projection.TelemetryStream(context.RequestAborted), eventType: "telemetry");

    private static HoldResponse ToResponse(HoldSnapshot hold) => new(
        hold.Id.Value,
        hold.CustomerId.Value,
        hold.Seats.Select(static seat => seat.Value).ToArray(),
        hold.Price.Amount,
        hold.Price.Currency,
        hold.ExpiresAt,
        hold.Status.ToString(),
        hold.PaymentId?.Value);

    private static AvailabilityResponse ToAvailability(Performance performance)
    {
        var expiryByHold = performance.Holds.ToDictionary(static hold => hold.Id, static hold => hold.ExpiresAt);
        return new AvailabilityResponse(
            performance.Id.Value,
            performance.Name,
            performance.StartsAt,
            performance.Version.Value,
            performance.Seats.Select(seat => new SeatResponse(
                seat.Id.Value,
                seat.State.ToString(),
                seat.HoldId is { } holdId && expiryByHold.TryGetValue(holdId, out var expiry) ? expiry : null,
                seat.Price.Amount,
                seat.Price.Currency)).ToArray());
    }

    private static ProblemDetails Problem(string code, string detail)
    {
        var problem = new ProblemDetails { Title = code.Replace('_', ' '), Detail = detail, Status = StatusCodes.Status409Conflict };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static Dictionary<string, object?> Code(string code) => new(StringComparer.Ordinal) { ["code"] = code };

    private sealed class SeatIdComparer : IEqualityComparer<SeatId>
    {
        public static SeatIdComparer OrdinalIgnoreCase { get; } = new();

        public bool Equals(SeatId x, SeatId y) => StringComparer.OrdinalIgnoreCase.Equals(x.Value, y.Value);

        public int GetHashCode(SeatId value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value);
    }
}
