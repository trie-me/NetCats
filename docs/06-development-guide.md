# Development guide

## What NetCats is for

NetCats is useful when the difficult part of a feature is not writing one asynchronous call, but preserving its meaning while calls are composed: when does it start, who owns it, how is cancellation observed, and when is cleanup actually complete?

`Task<T>` and `async`/`await` remain the native execution model. They are excellent at performing work. NetCats adds an optional description and ownership layer for workflows that need stronger, testable contracts:

- `Latent<T>` is cold: constructing and composing it performs no work;
- executing it through `RunAsync` is explicit and repeatable;
- failures can be recovered as part of the workflow;
- `FiberScope` owns child work and closes by requesting cancellation and joining it;
- `Outcome<T>` distinguishes success, cancellation, and failure after a fiber terminates;
- protected execution lets finalization complete after cancellation has been requested.

Use it at the application boundary, not in a pure domain model. A DDD aggregate should continue to express rules through methods, value objects, and domain events. It should not know about HTTP, repositories, `Latent<T>`, or cancellation tokens.

## The contract difference

An ordinary `Task<T>` can already be running when it reaches its caller:

```csharp
Task<Price> QuoteAsync(PerformanceId id, CancellationToken token) =>
    pricingClient.GetPriceAsync(id, token);
```

That is the right shape for an infrastructure port: the caller has decided to perform I/O.

At an application-workflow boundary, a cold description makes the start point intentional:

```csharp
Latent<HoldSeatsResult> HoldSeats(HoldSeatsCommand command) =>
    Latent<HoldSeatsResult>.DelayAsync(token => HoldSeatsAsync(command, token));
```

Neither form is universally superior. Choose `Task<T>` for a simple native API or an infrastructure adapter. Choose `Latent<T>` when callers must compose a workflow before running it, test its start boundary, or place it under a NetCats fiber/scope.

The key rule is: do not hide a started task inside a `Latent<T>` and call it cold. Use `DelayAsync` with a factory so each execution decides when to create the native operation.

## Minimal API pattern

Keep the handler thin and explicit. It converts HTTP input into an application command, executes the effect with `HttpContext.RequestAborted`, then maps a business value to a declared typed result.

```csharp
public static async Task<Results<Created<HoldResponse>, NotFound, Conflict<ProblemDetails>, ValidationProblem>> HoldSeats(
    Guid performanceId,
    HoldSeatsRequest request,
    BookingApplication application,
    CancellationToken cancellationToken)
{
    var result = await application.HoldSeats(ToCommand(performanceId, request))
        .RunAsync(cancellationToken);

    return result switch
    {
        HoldSeatsResult.Created created =>
            TypedResults.Created($"/performances/{performanceId}/holds/{created.Hold.Id.Value}", ToResponse(created.Hold)),
        HoldSeatsResult.NotFound => TypedResults.NotFound(),
        HoldSeatsResult.Unavailable unavailable =>
            TypedResults.Conflict(Problem("seats_unavailable", $"Seats unavailable: {string.Join(',', unavailable.Seats)}.")),
        HoldSeatsResult.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
        _ => throw new InvalidOperationException("Unknown hold result."),
    };
}
```

This separates three categories which should not be conflated:

| Category | Representation | HTTP treatment |
|---|---|---|
| Expected business result | A `HoldSeatsResult` case | Typed success, validation, conflict, or not-found result |
| Caller cancellation | `OperationCanceledException` from `RequestAborted` | Let ASP.NET Core abort the request; do not turn it into a domain error |
| Unexpected defect or infrastructure failure | Exception | Global exception handler and Problem Details |

Every `ProblemDetails` response should have a stable `code` extension. Client code can react to `seats_unavailable`, `hold_expired`, `payment_declined`, and `payment_timeout` without parsing prose.

## Building a DDD workflow

PurrfectSeat is the concrete reference implementation.

```text
HTTP handler
  -> application command returning Latent<BusinessResult>
    -> hydrate aggregate in a unit of work
      -> invoke pure aggregate behaviour
        -> persist snapshot with expected aggregate version
          -> commit and publish domain events
```

The aggregate in [PurrfectSeat.Domain](../examples/PurrfectSeat/src/PurrfectSeat.Domain) owns seat allocation and hold transitions. It is pure: no NetCats, ASP.NET Core, persistence, telemetry, or service clients.

The in-memory repository persists `PerformanceSnapshot` mementos. It does not retain mutable aggregates. A unit of work loads a snapshot, calls `Performance.Hydrate`, and persists a replacement snapshot only when the expected `PerformanceVersion` still matches. This makes optimistic concurrency part of the repository contract while keeping persistence mechanics outside the aggregate.

The application layer in [BookingApplication.cs](../examples/PurrfectSeat/src/PurrfectSeat.Application/BookingApplication.cs) owns I/O coordination and retry decisions. It checks eligibility and quotes a price concurrently, invokes the aggregate, and retries only an optimistic compare-and-swap loss. A real seat conflict is a terminal business result, not a blind retry.

## Working with fibers and scopes

Use a `FiberScope` when work is genuinely owned by a parent lifetime:

```csharp
await using var scope = new FiberScope();
var refresh = scope.Start(Latent<Availability>.DelayAsync(RefreshAvailabilityAsync));

// ... use the joined outcome if the result matters ...
var outcome = await refresh.JoinAsync();
```

Closing the scope asks every owned fiber to cancel and waits until its completion and deregistration are observed. Do not use a scope as a general replacement for background work; if work must survive a request, give it an application-lifetime owner such as a hosted service. PurrfectSeat's expiry worker is such an owner and uses a scope to make the worker's child lifetime explicit.

## A practical implementation checklist

1. Keep the domain project pure and model expected business failures as decisions or domain-rule violations.
2. Define application commands and closed result cases before defining HTTP responses.
3. Put native I/O behind small `Task`-based ports.
4. Wrap the application operation in a cold `Latent<T>` factory.
5. Execute it once at the boundary with the request cancellation token.
6. Map expected outcomes to explicit Minimal API `Results<...>` unions.
7. Use a unit of work and compare-and-swap version to protect an aggregate update.
8. Start fibers only under a scope whose owner is clear; always join or close that scope.
9. Add a test for the operation's cold boundary, one for the invariant, and one at a real native boundary.

## Running the example

From the repository root:

```shell
just examples-run
```

Open the Box Office at <http://localhost:5000/> and the demo-only Catwalk at <http://localhost:5000/control-room>. The recipe sets `ASPNETCORE_ENVIRONMENT=Demo`.

Related reading: [purpose and value](01-purpose-and-value.md), [production status](04-production-status.md), and the [PurrfectSeat implementation specification](05-purrfectseat-example-implementation.md).
