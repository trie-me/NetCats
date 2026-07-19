using System.Collections.Concurrent;
using PurrfectSeat.Application;
using PurrfectSeat.Contracts;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Scenarios;

public sealed class ScenarioRunner(BookingApplication application, IPerformanceReader performances)
{
    private static readonly string[] names =
    [
        "happy-path", "hot-seats", "slow-payments", "flaky-payments", "expiry-wave",
        "cancellation-storm", "shutdown-drain", "parallel-comparison", "bounded-comparison",
    ];

    public IReadOnlyList<string> Names => names;

    public async Task<ScenarioSummary> RunAsync(string name, ScenarioRequest request, CancellationToken cancellationToken)
    {
        if (!names.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Unknown scenario.");
        }

        if (request.Customers is < 1 or > 100 || request.MaxConcurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Customers must be 1..100 and max concurrency 1..32.");
        }

        var catalogue = await performances.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var performance = request.PerformanceId is { } performanceId
            ? catalogue.SingleOrDefault(item => item.Id.Value == performanceId)
            : catalogue.OrderBy(static item => item.StartsAt).FirstOrDefault();
        if (performance is null)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Choose an available performance showtime.");
        }

        var seats = performance.Seats.Where(static seat => seat.State == SeatState.Available).Select(static seat => seat.Id).ToArray();
        if (seats.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The selected performance has no available seats.");
        }
        var selected = new ConcurrentBag<HoldSeatsResult>();
        using var concurrency = new SemaphoreSlim(request.MaxConcurrency);
        var tasks = Enumerable.Range(0, Math.Min(request.Customers, 100)).Select(async customer =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var seat = seats[name == "hot-seats" ? 0 : customer % seats.Length];
                var result = await application.HoldSeats(new HoldSeatsCommand(
                    performance.Id,
                    HoldId.New(),
                    new CustomerId(Guid.NewGuid()),
                    new HashSet<SeatId> { seat })).RunAsync(cancellationToken).ConfigureAwait(false);
                selected.Add(result);
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var held = selected.OfType<HoldSeatsResult.Created>().ToArray();
        if (name is "happy-path" or "flaky-payments" or "slow-payments")
        {
            var token = name switch
            {
                "flaky-payments" => "nine-lives-card",
                "slow-payments" => "lazy-tabby",
                _ => "catnip-express",
            };
            await Task.WhenAll(held.Select(hold => application.ConfirmHold(new ConfirmHoldCommand(
                performance.Id,
                hold.Hold.Id,
                token,
                Guid.NewGuid().ToString("N"))).RunAsync(cancellationToken))).ConfigureAwait(false);
        }

        var finalPerformance = await application.GetPerformance(performance.Id).RunAsync(cancellationToken).ConfigureAwait(false);
        var confirmed = finalPerformance!.Holds.Count(static hold => hold.Status == HoldStatus.Confirmed);
        var noOversell = finalPerformance.Seats.Count(static seat => seat.State == SeatState.Confirmed) == finalPerformance.Holds
            .Where(static hold => hold.Status == HoldStatus.Confirmed)
            .Sum(static hold => hold.Seats.Count);
        return new ScenarioSummary(
            name,
            request.Customers,
            held.Length,
            confirmed,
            selected.Count(static result => result is HoldSeatsResult.Unavailable),
            noOversell);
    }
}
