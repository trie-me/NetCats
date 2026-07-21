# PurrfectSeat.com Example Solution: Implementation Specification

## Status

Proposed example application and API-design target.

This document specifies an examples solution that demonstrates why NetCats is useful in an ordinary .NET business application. It deliberately exercises cold effects, resource safety, structured concurrency, cancellation, time, retries, race handling, deterministic tests, and telemetry without turning NetCats into an application framework.

The example is named **PurrfectSeat.com**: a shamelessly enthusiastic, cat-themed concert ticket marketplace with a small live operations dashboard. Code projects and namespaces use the valid identifier `PurrfectSeat`.

## Goals

PurrfectSeat.com should:

- be an ASP.NET Core .NET 10 Minimal API;
- return `TypedResults` and declared `Results<T1, ...>` unions from every business endpoint;
- contain a small but credible DDD model;
- use NetCats in the application layer rather than contaminating the domain model;
- demonstrate both simple and failure-heavy workflows;
- provide a usable fake customer booking frontend as well as an operator dashboard;
- commit fully to a playful feline ticket-hawker identity without hiding real system state;
- make cancellation, fibers, finalizers, retries, conflicts, queues, and latency visible;
- consume the reusable NetCats ASP.NET Core fiber-tree stream rather than inventing an example-specific scope protocol;
- run as a single process with simulated infrastructure;
- require no database, container runtime, JavaScript build chain, or external telemetry stack;
- remain suitable for integration tests, deterministic tests, and live demonstrations;
- provide optional OpenTelemetry export without requiring it for the dashboard.

## Non-goals

PurrfectSeat.com will not initially include:

- authentication or authorization;
- a production database;
- event sourcing;
- multiple deployed services;
- real payment processing;
- a frontend framework or Node-based toolchain;
- a complete ticketing platform;
- realistic personal data;
- performance claims based solely on the demo dashboard.

The demo exists to explain behaviour and trade-offs. Formal NetCats performance claims remain the responsibility of the benchmark projects.

## Business premise

A venue publishes performances with individually addressable seats. A customer may hold available seats for a limited period. A hold can then be confirmed through a simulated payment provider, cancelled explicitly, or expired automatically.

Many customers may compete for the same seats. The system must never confirm a seat twice, must not leak background work, and must release expired holds predictably.

## Domain model

### Aggregate root: `Performance`

`Performance` owns the seat-allocation invariant for one event occurrence.

```text
Performance
├── PerformanceId
├── Name
├── StartsAt
├── Version
├── Seats
└── Holds
```

The aggregate exposes behaviour rather than public collection mutation:

```csharp
HoldPlaced HoldSeats(
    HoldId holdId,
    CustomerId customerId,
    IReadOnlySet<SeatId> seats,
    Money price,
    DateTimeOffset expiresAt);

HoldConfirmed ConfirmHold(
    HoldId holdId,
    PaymentId paymentId,
    DateTimeOffset confirmedAt);

HoldCancelled CancelHold(
    HoldId holdId,
    DateTimeOffset cancelledAt);

HoldExpired ExpireHold(
    HoldId holdId,
    DateTimeOffset observedAt);
```

### Value objects

- `PerformanceId`
- `HoldId`
- `CustomerId`
- `SeatId`
- `PaymentId`
- `Money`
- `PerformanceVersion`

Identifiers should be small immutable record structs. `Money` includes currency and rejects invalid arithmetic across currencies.

### State

```csharp
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
```

### Invariants

- A seat may belong to at most one active or confirmed allocation.
- A hold must contain at least one seat.
- A confirmed hold cannot be cancelled or expired.
- An expired hold cannot be confirmed.
- A hold can transition from active exactly once.
- The quoted price is fixed for the lifetime of the hold.
- Aggregate versions increase on every persisted change.

### Domain events

- `SeatsHeld`
- `HoldConfirmed`
- `HoldCancelled`
- `HoldExpired`

Domain events contain identifiers and business facts. They do not contain `Latent<T>`, HTTP types, persistence types, or runtime services.

## Application architecture

```text
Minimal API endpoint
      |
      v
Application command returning Latent<BusinessResult>
      |
      +--> pure Performance aggregate
      +--> repository / unit of work ports
      +--> payment, pricing and customer ports
      +--> expiry queue and domain-event outbox
      |
      v
Typed business result
      |
      v
TypedResults HTTP mapping
```

Expected business failures are values. Unexpected infrastructure and programming failures remain exceptions and are handled by a global `IExceptionHandler` and `IProblemDetailsService`.

Cancellation caused by `HttpContext.RequestAborted` is not converted into an ordinary domain failure.

## Proposed solution layout

```text
examples/
  PurrfectSeat/
    NetCats.Examples.PurrfectSeat.slnx
    Directory.Build.props
    README.md

    src/
      PurrfectSeat.Domain/
      PurrfectSeat.Application/
      PurrfectSeat.Infrastructure/
      PurrfectSeat.Contracts/
      PurrfectSeat.Api/
      PurrfectSeat.Scenarios/

    tests/
      PurrfectSeat.Domain.Tests/
      PurrfectSeat.Application.Tests/
      PurrfectSeat.Api.Tests/
      PurrfectSeat.Scenario.Tests/
```

### Project responsibilities

#### `PurrfectSeat.Domain`

Pure aggregates, value objects, domain events, and domain exceptions. It has no NetCats, ASP.NET Core, persistence, or telemetry dependency.

#### `PurrfectSeat.Application`

Commands, queries, typed business results, ports, and NetCats workflows. It references `PurrfectSeat.Domain`, `NetCats.Core`, and the smallest required runtime abstractions.

#### `PurrfectSeat.Infrastructure`

In-memory optimistic repository, asynchronous unit of work, configurable payment/pricing/customer simulators, outbox, hold-expiry queue, and telemetry adapters.

#### `PurrfectSeat.Contracts`

Minimal API request/response records and shared telemetry DTOs. Contracts do not expose domain aggregates directly.

#### `PurrfectSeat.Api`

The .NET 10 Minimal API host, endpoint mappings, `TypedResults` conversions, exception handling, public availability and demo telemetry SSE endpoints, opt-in NetCats fiber diagnostics endpoints, hosted expiry worker, and both static web surfaces under `wwwroot`.

#### `PurrfectSeat.Scenarios`

Reusable demo/load scenarios driven through a typed `HttpClient`. The dashboard can start and stop scenarios through demo-only endpoints, and integration tests can reuse the same drivers.

## Proposed NetCats surface exercised by PurrfectSeat.com

The example targets these additional high-level APIs:

```csharp
Latent.Pure(value)
Latent.Delay(factory)
Latent.FromTask(factory)
Latent.Sleep(duration)

effect.Map(selector)
effect.Bind(selector)
effect.Tap(action)
effect.Guarantee(finalizer)
effect.GuaranteeCase(finalizer)
effect.OnCancel(finalizer)
effect.TimeoutAfter(duration)
effect.Retry(schedule)
effect.Start()
effect.Background()

Latent.Parallel(...)
Latent.TraverseParallel(values, maxConcurrency, selector)
Latent.Race(...)
Latent.RaceSuccess(...)
Latent.Scoped(...)

Resource.Acquire(...)
Resource.FromDisposable(...)
Resource.FromAsyncDisposable(...)
resource.Use(...)

Ref.Create(value)
Deferred.Create<T>()
Queue.Bounded<T>(capacity)
```

Where production NetCats does not yet expose an operation, the example must not recreate incompatible semantics locally. Implement and test the NetCats primitive first, or keep the scenario pending.

## HTTP API

All business handlers are named methods with explicit typed unions. Avoid anonymous handlers returning `IResult`.

```csharp
var performances = app.MapGroup("/performances/{performanceId:guid}");

performances.MapPost("/holds", PurrfectSeatEndpoints.HoldSeats);
performances.MapPost("/holds/{holdId:guid}/confirm", PurrfectSeatEndpoints.ConfirmHold);
performances.MapDelete("/holds/{holdId:guid}", PurrfectSeatEndpoints.CancelHold);
performances.MapGet("/holds/{holdId:guid}", PurrfectSeatEndpoints.GetHold);
performances.MapGet("/availability", PurrfectSeatEndpoints.GetAvailability);
performances.MapGet("/availability/stream", PurrfectSeatEndpoints.StreamAvailability);
```

### Create hold

```http
POST /performances/{performanceId}/holds
```

Request:

```csharp
public sealed record HoldSeatsRequest(
    Guid CustomerId,
    IReadOnlyList<string> Seats);
```

Response union:

```csharp
Task<Results<
    Created<HoldResponse>,
    NotFound,
    Conflict<ProblemDetails>,
    ValidationProblem>>
```

Outcomes:

- `201 Created`: hold created;
- `404 Not Found`: performance does not exist;
- `409 Conflict`: one or more seats are unavailable after retries;
- `400 Validation Problem`: malformed identifiers, empty seats, or duplicate seats.

### Confirm hold

```http
POST /performances/{performanceId}/holds/{holdId}/confirm
```

Request:

```csharp
public sealed record ConfirmHoldRequest(
    string PaymentMethodToken,
    string IdempotencyKey);
```

Response union:

```csharp
Task<Results<
    Ok<HoldResponse>,
    NotFound,
    Conflict<ProblemDetails>,
    UnprocessableEntity<ProblemDetails>,
    ProblemHttpResult>>
```

Outcomes:

- `200 OK`: already confirmed with the same idempotency key, or newly confirmed;
- `404 Not Found`: performance or hold does not exist;
- `409 Conflict`: hold is expired, cancelled, or lost an optimistic concurrency race;
- `422 Unprocessable Entity`: payment declined;
- `504 Problem`: payment authorization timed out after the configured policy.

### Cancel hold

```http
DELETE /performances/{performanceId}/holds/{holdId}
```

Response union:

```csharp
Task<Results<
    NoContent,
    NotFound,
    Conflict<ProblemDetails>>>
```

### Read hold

```http
GET /performances/{performanceId}/holds/{holdId}
```

Response union:

```csharp
Task<Results<Ok<HoldResponse>, NotFound>>
```

### Read availability

```http
GET /performances/{performanceId}/availability
```

Response union:

```csharp
Task<Results<Ok<AvailabilityResponse>, NotFound>>
```

### Stream availability

```http
GET /performances/{performanceId}/availability/stream
```

The customer frontend uses a typed server-sent event stream to observe committed seat-state changes. The endpoint returns `TypedResults.ServerSentEvents(...)` over `SseItem<AvailabilityEvent>` values. It never exposes uncommitted aggregate mutations.

### Typed result mapping

Application operations return discriminated business outcomes inside `Latent<T>`:

```csharp
Latent<HoldSeatsResult> HoldSeats(HoldSeatsCommand command);
Latent<ConfirmHoldResult> ConfirmHold(ConfirmHoldCommand command);
Latent<CancelHoldResult> CancelHold(CancelHoldCommand command);
```

Endpoint handlers execute the effect with the request token and map the result:

```csharp
var result = await application
    .HoldSeats(command)
    .RunAsync(cancellationToken);

return result switch
{
    HoldSeatsResult.Created created =>
        TypedResults.Created(created.Location, HoldResponse.From(created.Hold)),

    HoldSeatsResult.NotFound =>
        TypedResults.NotFound(),

    HoldSeatsResult.Unavailable unavailable =>
        TypedResults.Conflict(Problems.SeatsUnavailable(unavailable.Seats)),

    HoldSeatsResult.Invalid invalid =>
        TypedResults.ValidationProblem(invalid.Errors),

    _ => throw new UnreachableException(),
};
```

Every `ProblemDetails` payload includes a stable machine-readable `code` extension such as `seats_unavailable`, `hold_expired`, `payment_declined`, or `payment_timeout`.

## Baseline workflows

### Place a hold

The application concurrently retrieves customer eligibility and current pricing, then opens a unit of work, loads the aggregate, applies the domain decision, persists, commits, and schedules expiry.

```csharp
var context = Latent.Parallel(
    customers.CheckEligibility(command.CustomerId),
    pricing.Quote(command.PerformanceId, command.Seats));

return context.Bind(result =>
    unitOfWork.Use(uow =>
        repository.Load(command.PerformanceId)
            .Map(performance => performance.HoldSeats(
                command.HoldId,
                command.CustomerId,
                command.Seats,
                result.Price,
                time.GetUtcNow() + options.HoldDuration))
            .Bind(uow.Save)
            .Bind(saved => uow.Commit().Map(_ => saved))))
    .Retry(Policies.OptimisticConcurrency);
```

This demonstrates cold construction, parallel effects, resource-scoped transactions, pure domain logic, optimistic retries, and cancellation propagation.

### Cancel a hold

Cancellation is an ordinary aggregate transition. The request transaction is protected, events are placed in the outbox, and the worker ignores a later stale expiry item.

### Read availability

The query returns a compact DTO with seat state, price band, hold expiry where relevant, and aggregate version. It does not return the aggregate.

## Complex workflows

### Confirmation versus expiry race

A payment confirmation may arrive while the expiry worker is attempting to expire the same hold. Both paths load the same aggregate version and attempt a conditional save.

Exactly one transition wins:

- confirmation wins: the expiry worker observes the confirmed state and becomes a no-op;
- expiry wins: confirmation returns `hold_expired` and any provisional payment authorization is voided.

The demo should expose the race as a repeatable scenario and prove that a seat is never both confirmed and released.

### Payment authorization as a managed resource

Confirmation treats a provisional payment authorization as a resource:

```csharp
var authorization = Resource.Acquire(
    payments.Authorize(command.Payment, command.IdempotencyKey)
        .TimeoutAfter(options.PaymentTimeout)
        .Retry(Policies.Payment),
    (payment, exit) => exit == ExitCase.Succeeded
        ? Latent.Pure(Unit.Value)
        : payments.Void(payment.Id));

return authorization.Use(payment =>
    ConfirmAndCommit(command, payment));
```

If persistence fails, the request is cancelled, or expiry wins, the authorization is voided. A successful commit transfers ownership and suppresses compensation according to the final resource contract.

### Idempotent confirmation

Repeated confirmation requests with the same idempotency key return the existing confirmation. Reusing the key with different payment details returns a conflict. Simulated payment calls also honour the key so retry cannot produce duplicate authorization.

### Optimistic-concurrency storm

Many customers request overlapping seat sets. Each attempt may retry only when the aggregate version changed and the requested seats remain potentially available. Once the domain reports an actual seat conflict, the operation terminates with `409` rather than retrying blindly.

### Payment degradation

The payment simulator can add latency, time out, decline, or fail transiently. Retry applies only to transient failures. The demo distinguishes:

- attempt count;
- eventual success;
- terminal decline;
- timeout;
- caller cancellation;
- compensation success or failure.

### Client cancellation storm

The scenario starts many confirmations and cancels their HTTP requests at controlled points. The dashboard must show:

- cancellation requests rising;
- finalizers executing;
- provisional authorizations being voided;
- active fibers returning to baseline;
- no open unit of work remaining.

### Background expiry backlog

The expiry queue is bounded. A burst of holds can create queue pressure without unbounded memory growth. The worker processes items with bounded parallelism and exposes queue depth, age of oldest item, execution latency, and expired/no-op counts.

### Graceful application shutdown

The expiry worker and telemetry broadcaster run inside application-lifetime NetCats scopes. Shutdown requests cancellation, joins children, drains required finalizers, and reports the final active-fiber count before the host exits.

## Simulated infrastructure

### Optimistic repository

The repository stores serialized aggregate snapshots in memory and updates them through compare-and-swap on `PerformanceVersion`. It can inject configurable read/write latency and transient failures.

### Asynchronous unit of work

The unit of work stages aggregate and outbox changes until commit. `DisposeAsync` rolls back uncommitted work and publishes telemetry showing whether commit or rollback occurred.

### Payment provider

Configuration:

```csharp
public sealed record PaymentSimulation(
    TimeSpan MinimumLatency,
    TimeSpan MaximumLatency,
    double TransientFailureRate,
    double DeclineRate,
    double TimeoutRate);
```

Randomness is injected through a deterministic interface so scenario tests can replay a seed.

### Pricing and customer services

These simple ports simulate independent I/O so the UI can compare sequential and parallel composition without fabricating CPU benchmark claims.

### Outbox and event feed

Committed domain events enter an in-memory outbox. A supervised publisher produces the customer availability stream, dashboard activity feed, and optional Rx projection. Outbox publication is at-least-once, so projections are idempotent by event ID.

## Brand identity

### Name and proposition

The public brand is **PurrfectSeat.com**.

Primary line:

> Find the purrfect seat before another cat does.

Supporting line:

> Seats move fast. Our effects stay lawful.

The customer experience behaves like an over-eager ticket marketplace: bright calls to action, scarcity messages, countdowns, social activity, and relentless encouragement to check out. The humour should make the sales pressure obviously theatrical rather than genuinely deceptive.

### Voice

Copy is concise, excitable, and full of restrained cat wordplay:

- “Pounce on these seats”;
- “Don’t paws now—your hold expires in 01:12”;
- “Nine other cats are prowling this row”;
- “That seat has left the litter”;
- “Your tickets are in the bag”;
- “Payment coughed up a hairball. We’re trying again”;
- “Sold out? Cat-astrophic.”

Do not replace important technical or accessibility language with jokes. Status, price, expiry, errors, and actions must remain unambiguous.

### Visual system

Use a distinctive but accessible palette:

- midnight navy for headers and stage furniture;
- warm cream for the page background;
- electric coral for primary purchase actions;
- catnip green for available/success states;
- amber for expiring holds and retries;
- deep plum for confirmed seats;
- restrained paw-print and whisker motifs as decoration.

The logo can combine a ticket stub with pointed cat ears. Seat markers may gain subtle ear corners, but their availability state must still be communicated by text, shape, and accessible labels rather than colour alone.

Typography should feel like a credible ticket marketplace with one display face for headlines and a highly legible system face for prices, timers, forms, and telemetry.

### Shameless ticket hawking

The Box Office deliberately includes simulated sales-pressure components:

- “popular row” and “selling fast” ribbons;
- a live count of simulated shoppers viewing the performance;
- visible hold countdowns;
- animated seat-state changes;
- a sticky checkout panel;
- emphatic primary purchase actions;
- celebratory confirmation treatment.

All claims must be derived from actual simulated state. The UI must not invent scarcity, viewers, or competing holds. This preserves the joke while keeping the system semantically honest.

### Product surface names

- **PurrfectSeat.com Box Office**: customer booking frontend;
- **The Catwalk**: operator dashboard subtitle;
- **Effect Control Room**: explicit technical description shown under The Catwalk;
- `PurrfectSeat` remains the project, namespace, meter, and contract prefix.

## PurrfectSeat.com web experience

### Delivery model

The example contains two connected web surfaces:

- **PurrfectSeat.com Box Office**: the fake customer-facing application;
- **The Catwalk — Effect Control Room**: the operator telemetry and scenario dashboard.

Both are static HTML, CSS, and modern browser JavaScript served from `PurrfectSeat.Api/wwwroot`. They use no frontend framework and no build step.

Use:

- regular `fetch` for commands and snapshots;
- `EventSource` for live server-sent events;
- CSS Grid for layout;
- SVG or Canvas for small charts;
- accessible colours plus text/shape indicators for seat state.

Suggested asset layout:

```text
wwwroot/
  index.html
  control-room.html
  css/
    site.css
    box-office.css
    control-room.css
    fiber-tree-overlay.css
  js/
    api-client.js
    event-stream.js
    box-office.js
    control-room.js
    fiber-tree-overlay.js
    seat-map.js
    charts.js
```

The Box Office opens at `/`. The Catwalk opens at `/control-room`. Both use the same seat-map component and public API contracts, but only the Catwalk calls `/demo/*` endpoints.

## PurrfectSeat.com Box Office

### Purpose

The customer UI must be a functioning fake frontend, not a decorative wrapper around the dashboard. A user can browse a performance, select seats, place a temporary hold, complete simulated payment, cancel a hold, and see confirmation.

The browser uses only public business endpoints. It must never mutate the in-memory simulator or repository directly.

### Customer journey

1. Open the performance page and load current availability.
2. Select available seats and view the calculated total.
3. Submit customer details to create a hold.
4. Display the server-provided expiry time as a countdown.
5. Complete checkout with a selectable fake payment method.
6. Show confirmation, decline, timeout, expiry, cancellation, or conflict clearly.
7. Allow an active hold to be cancelled.
8. Reconcile the UI when another customer changes seat availability.

### Page layout

```text
+------------------------------------------------------------------+
| PurrfectSeat.com       Find your purrfect seat   The Catwalk ↗    |
+------------------------------------------------------------------+
| Midnight at the Apollo | 19:30 | Nine cats browsing this row     |
+--------------------------------------+---------------------------+
| Stage                                | Your selection            |
|                                      | A12, A13                   |
| [A][A][H][B] ... seat map            | £48.00                     |
|                                      | [Pounce on these seats]    |
+--------------------------------------+---------------------------+
| Status: Live availability connected  | Checkout / confirmation   |
+------------------------------------------------------------------+
```

### Customer seat map

States are visually and textually distinct:

- available;
- selected by this browser;
- held by this browser;
- held by another customer;
- confirmed/unavailable;
- state unknown while reconnecting.

Selection is optimistic only in the browser. The aggregate remains authoritative, and a `409 seats_unavailable` response immediately reconciles the map with the returned or freshly loaded availability.

The seat map supports keyboard navigation, visible focus, non-colour status indicators, and an accessible textual selection summary.

### Hold panel

After `201 Created`, display:

- selected seats and fixed quoted price;
- hold identifier shortened for display;
- absolute server expiry time;
- countdown derived from the server time response;
- confirm and cancel actions;
- a warning when little time remains.

The countdown is advisory. The server decides whether the hold expired.

### Fake checkout

Offer clearly labelled simulated methods:

- **Catnip Express** — succeeds quickly;
- **Lazy Tabby** — succeeds slowly;
- **Bank of Meow Decline** — declines;
- **Nine Lives Card** — fails transiently before success;
- **Hairball Gateway** — times out.

These choices select documented fake payment tokens; they do not call demo-control endpoints. The resulting business request still follows the normal confirmation workflow and typed HTTP contract.

Double submission is prevented in the UI, while the API remains safe through an idempotency key generated per checkout intent.

### Live availability

The frontend subscribes to:

```http
GET /performances/{performanceId}/availability/stream
```

The stream updates committed seat changes from other users and automated scenarios. It carries monotonically increasing event IDs. On disconnect, gap detection, or stale version, the browser reloads the availability snapshot before resuming.

The customer's own active hold is stored in session storage so a page refresh can reload the hold through the public read endpoint. No payment token is persisted in the browser.

### Customer-facing errors

Map stable Problem Details codes to clear messages:

| Code | Customer message |
|---|---|
| `seats_unavailable` | One or more selected seats were just taken. Choose again. |
| `hold_expired` | Your hold expired before payment completed. |
| `payment_declined` | The simulated payment was declined. |
| `payment_timeout` | The payment provider did not respond in time. |
| `confirmation_conflict` | The reservation changed while checkout was completing. |

Unexpected problems show the correlation ID and a retry-safe action without exposing exception details.

## The Catwalk — Effect Control Room

### Purpose

The Catwalk explains the same business activity from an operational and NetCats-runtime perspective. It can launch reproducible scenarios while the Box Office remains usable in another browser window. Cat-themed labels may accompany technical labels, but must never replace units or operational meaning.

### Page layout

```text
+------------------------------------------------------------------+
| PurrfectSeat.com — The Catwalk   Scenario | Run | Stop | Reset    |
+------------------------------------------------------------------+
| Throughput | p95 | Active fibers | Queue | Conflicts | Timeouts  |
+------------------------------+-----------------------------------+
| Seat map                     | Scenario controls                 |
| [A][A][H][B] ...             | users, concurrency, latency       |
| available/held/booked        | failure %, hold duration, seed    |
+------------------------------+-----------------------------------+
| Request latency chart        | NetCats runtime state             |
| p50 / p95 / p99              | active/scoped/cancel/finalize     |
+------------------------------+-----------------------------------+
| Domain activity feed                                             |
| held → payment started → confirmed / expired / compensated       |
+------------------------------------------------------------------+
```

### Seat map

The Catwalk reuses the customer seat map but overlays operational detail. Each seat displays:

- available, held, or confirmed state;
- active hold expiry countdown;
- a short customer colour/label during scenarios;
- a pulse when its state changes;
- a tooltip containing the last domain transition and aggregate version.

Do not expose customer IDs or hold IDs as metric dimensions. They may appear in sampled traces and the local demo feed.

### Scenario controls

Controls:

- scenario selector;
- virtual customer count;
- maximum concurrency;
- selected seat overlap;
- payment latency range;
- transient failure, decline, and timeout rates;
- hold duration;
- deterministic random seed;
- real versus manual time;
- start, stop, reset, and advance-time buttons.

Settings are submitted to typed demo endpoints and validated through `ValidationProblem` responses.

Launching a scenario must not disable manual use of the Box Office. This allows a user to compete with simulated customers and observe the resulting conflicts in both views.

### KPI strip

Show current and rolling values:

- completed HTTP requests per second;
- p50, p95, and p99 end-to-end latency;
- active HTTP requests;
- active NetCats fibers;
- cancellation requests;
- finalizers currently running;
- optimistic conflicts and retries;
- payment timeouts and declines;
- expiry queue depth;
- available, held, and confirmed seat counts.

Playful aliases can sit beneath the real metric labels—for example “pounces/sec” for request throughput, “lives used” for retries, and “hairballs” for failures—but the technical name, unit, and value remain primary.

### Runtime panel

Display the compact logical ownership tree produced by the reusable `NetCats.AspNetCore` diagnostics adapter:

```http
GET /_netcats/fibers/snapshot
GET /_netcats/fibers/stream
```

The panel opens an `EventSource` only while visible. The first event is a complete versioned tree and later events are coalesced complete snapshots, allowing slow or reconnecting clients to render current state without backpressuring runtime fibers.

Example presentation:

```text
Application scope
├── expiry worker       running
├── outbox publisher    running
├── telemetry stream    running
└── scenario 42
    ├── customer 001    payment
    ├── customer 002    retry 2
    └── customer 003    finalizing
```

This is a structured ownership view only. Do not display or imply thread ownership, an async call graph, or stack traces. The endpoint is read-only and never exposes effect values, exception objects, customer identifiers, payment values, or control operations.

### Activity feed

The feed interleaves domain and runtime events:

```text
12:00:01.120  Hold H42 placed for A1,A2; expires in 30 s
12:00:01.184  Payment P7 attempt 1 started
12:00:01.245  Hold H43 rejected: A2 unavailable
12:00:02.010  Request C18 cancellation requested
12:00:02.014  Payment P9 void finalizer completed
12:00:02.120  Hold H42 confirmed
```

Allow filtering by event category, scenario, outcome, and correlation ID.

## Telemetry design

### Sources

Use standard .NET telemetry primitives:

- `ActivitySource` for traces;
- `System.Diagnostics.Metrics.Meter` for counters, gauges, and histograms;
- structured `ILogger` events for diagnostics;
- a bounded in-memory event channel for the demo SSE projection.

The UI projection is not the authoritative metrics backend. It is a bounded, rolling demo view. Optional OpenTelemetry configuration may export the same instrumentation to Prometheus, OTLP, or another backend.

### Meter

Use one example meter:

```text
NetCats.Examples.PurrfectSeat
```

Proposed instruments:

| Instrument | Type | Meaning |
|---|---|---|
| `seatsafe.holds.created` | Counter | Successfully created holds |
| `seatsafe.holds.expired` | Counter | Holds expired by the worker |
| `seatsafe.holds.confirmed` | Counter | Confirmed holds |
| `seatsafe.seats.conflicts` | Counter | Domain seat conflicts |
| `seatsafe.repository.conflicts` | Counter | Optimistic version conflicts |
| `seatsafe.operation.retries` | Counter | Retry attempts by operation |
| `seatsafe.payment.attempts` | Counter | Payment authorization attempts |
| `seatsafe.payment.compensations` | Counter | Payment void attempts |
| `seatsafe.operation.duration` | Histogram | Application-operation latency |
| `seatsafe.payment.duration` | Histogram | Payment-provider latency |
| `seatsafe.expiry.lag` | Histogram | Delay beyond scheduled expiry |
| `seatsafe.fibers.active` | Up/down counter | Active logical fibers |
| `seatsafe.finalizers.active` | Up/down counter | Running finalizers |
| `seatsafe.expiry.queue.depth` | Observable gauge | Current bounded-queue depth |
| `seatsafe.unit_of_work.active` | Up/down counter | Open units of work |

Use low-cardinality tags such as operation, outcome, retry reason, and scenario type. Never tag metrics with customer, performance, hold, payment, seat, correlation, or fiber IDs.

### Activities

Proposed activity names:

```text
seatsafe.hold.create
seatsafe.hold.confirm
seatsafe.hold.cancel
seatsafe.hold.expire
seatsafe.repository.load
seatsafe.repository.save
seatsafe.payment.authorize
seatsafe.payment.void
seatsafe.outbox.publish
```

Activities may carry correlation identifiers because traces are sampled records rather than metric dimensions.

### Telemetry stream

The .NET 10 host exposes a typed SSE endpoint:

```http
GET /demo/telemetry/stream
```

It returns `TypedResults.ServerSentEvents(...)` over an `IAsyncEnumerable<SseItem<TelemetryEvent>>`. Events include monotonically increasing IDs so the client can detect gaps and refresh `/demo/telemetry/snapshot`.

The source channel is bounded. Slow dashboard clients receive coalesced metric snapshots rather than causing unbounded server memory growth.

### Snapshot endpoint

```http
GET /demo/telemetry/snapshot
```

Returns:

```csharp
Task<Ok<TelemetrySnapshot>>
```

The snapshot contains the current seat state, active scenario, rolling histograms, queue depth, and latest feed cursor. It does not duplicate the fiber tree; the generic NetCats diagnostics endpoints are authoritative for that projection.

## Demo-only endpoints

Demo endpoints are enabled only in the `Demo` environment.

```http
GET  /demo/scenarios
POST /demo/scenarios/{name}/start
POST /demo/scenarios/current/stop
POST /demo/reset
PUT  /demo/simulation
POST /demo/time/advance
GET  /demo/telemetry/snapshot
GET  /demo/telemetry/stream
GET  /_netcats/fibers/snapshot
GET  /_netcats/fibers/stream
```

All finite endpoints return `TypedResults`. The SSE endpoints use `TypedResults.ServerSentEvents`. The `/_netcats/fibers/*` endpoints are supplied by the optional reusable NetCats ASP.NET Core adapter and mapped only in the `Demo` environment for this example.

## Meaningful performance scenarios

### 1. Sequential versus parallel independent I/O

Run customer eligibility and pricing sequentially, then through `Latent.Parallel`, under the same deterministic latency. Show end-to-end latency and verify identical business output.

This demonstrates latency composition, not raw CPU speed.

### 2. Bounded versus uncontrolled fan-out

Process a large availability or hold workload with a configurable concurrency limit. Show throughput, p95 latency, queue depth, and active fibers.

The lesson is controlled resource usage rather than “more parallel is always faster.”

### 3. Contended seat allocation

Run 100 virtual customers against a small overlapping seat set. Show conflicts, optimistic retries, successful holds, and the invariant that confirmed seats never exceed capacity.

### 4. Payment degradation and retry amplification

Increase transient failure and latency. Show when bounded retry improves success and when it increases latency or load. Compare fixed and exponential schedules with the same seeded failure sequence.

### 5. Cancellation cleanup

Cancel a large in-flight checkout set. Show cancellation-to-termination latency, compensation count, finalizer latency, open units of work, and active fibers returning to zero.

### 6. Expiry burst under virtual time

Create many holds, advance manual time past their expiry, and observe bounded expiry processing. Show queue depth and expiry lag without waiting for wall-clock minutes.

### 7. Graceful shutdown

Stop the host during payment and expiry activity. Show the logical scope draining, finalizers completing, and no owned fibers remaining.

## Scenario definitions

Initial named scenarios:

- `happy-path`: a few non-overlapping customers;
- `hot-seats`: high contention on a small seat set;
- `slow-payments`: increased payment latency;
- `flaky-payments`: transient failures with bounded retry;
- `expiry-wave`: many short holds with manual time;
- `cancellation-storm`: client aborts during confirmation;
- `shutdown-drain`: host shutdown during mixed work;
- `parallel-comparison`: sequential versus parallel independent I/O;
- `bounded-comparison`: bounded versus uncontrolled fan-out.

Each scenario accepts a seed and emits a completion summary with expected invariants.

## Testing strategy

### Domain tests

Pure tests cover aggregate transitions and invariants without NetCats or ASP.NET Core.

### Application tests

Use deterministic ports and `ManualTimeProvider`. Cover business-result values, retry limits, compensation, cancellation, and expiry/confirmation races.

### API tests

Run the Minimal API in-process. Assert concrete `TypedResults` behaviour, JSON contracts, Problem Details codes, request cancellation, and OpenAPI metadata.

### Scenario tests

Run every named scenario with fixed seeds. Assert:

- no double confirmation;
- seat-count conservation;
- no active unit of work after completion;
- no owned fibers after scenario shutdown;
- every provisional authorization is committed or voided;
- bounded queue capacity is never exceeded;
- telemetry cardinality remains bounded.

### UI smoke tests

Test:

- Box Office static asset availability;
- performance and availability rendering;
- seat selection and hold creation;
- successful, declined, timed-out, and expired checkout states;
- cancellation and page-refresh hold recovery;
- conflict reconciliation when another customer wins a seat;
- public availability SSE reconnect and gap refresh;
- Catwalk snapshot rendering;
- telemetry SSE reconnect and gap refresh;
- fiber overlay open/close, nested scope rendering, lifecycle changes, and SSE reconnect;
- scenario start/stop and manual-time controls.

Full visual-regression infrastructure is not required initially.

## Implementation sequence

### Phase 1: Domain and contracts

- Implement aggregate, value objects, events, business results, and DTOs.
- Add pure domain tests.
- Define endpoint unions and Problem Details codes.

### Phase 2: Simple application workflows

- Implement read availability, create hold, cancel hold, and confirm hold.
- Use the currently available NetCats surface where semantics match.
- Add in-memory repository and unit of work.
- Add Minimal API endpoints with `TypedResults`.

### Phase 3: NetCats showcase primitives

- Promote named scope/fiber descriptors, non-blocking lifecycle observation, and the optional ASP.NET Core fiber diagnostics adapter.
- Implement and promote `Resource<T>`.
- Add parallel composition and bounded traversal.
- Add timeout and retry schedule support.
- Add `Background`, scoped start, bounded queue, and deterministic time integration.
- Validate each primitive in NetCats before using it in PurrfectSeat.com.

### Phase 4: Complex cases

- Implement payment authorization resource/compensation.
- Implement expiry worker and confirmation/expiry race.
- Implement idempotency and optimistic retry.
- Add cancellation-storm and graceful-shutdown scenarios.

### Phase 5: Telemetry and UI

- Build the PurrfectSeat.com Box Office against public endpoints.
- Add the shared accessible seat-map component and public availability SSE stream.
- Implement hold recovery, countdown, checkout, cancellation, and typed error presentation.
- Add activities, metrics, and bounded telemetry projection.
- Add typed snapshot and SSE endpoints.
- Build the static Catwalk — Effect Control Room.
- Add scenario controls, seat map, charts, the reusable fiber-tree overlay, and activity feed.

### Phase 6: Performance demonstrations and hardening

- Implement repeatable seeded scenario comparisons.
- Document what each comparison does and does not prove.
- Add optional OpenTelemetry export.
- Verify bounded memory, low-cardinality metrics, graceful shutdown, and no fiber leaks.

## Acceptance criteria

The example is complete when:

- all business endpoints are Minimal API handlers with explicit `TypedResults` unions;
- the domain project has no NetCats or ASP.NET dependency;
- ordinary success, validation, not-found, conflict, decline, and timeout paths are visible in OpenAPI;
- a customer can select seats, create a hold, refresh the page, confirm or cancel the hold, and receive a typed final state through the Box Office;
- the Box Office uses only public business endpoints and remains functional when demo-control endpoints are disabled;
- live availability reconciles concurrent changes and recovers correctly after an SSE gap;
- 100 contending customers cannot oversell seats;
- confirmation/expiry races are deterministic and preserve invariants;
- cancelled confirmation voids provisional payment and closes its unit of work;
- the expiry worker is bounded and shuts down without owned fibers;
- the dashboard reconnects to typed SSE without leaking subscriptions;
- the Catwalk renders the reusable versioned NetCats fiber tree while scenarios run, and closing the panel closes its stream;
- slow fiber-tree clients cannot backpressure or alter observed fiber execution;
- the Box Office and Catwalk can run concurrently against the same performance state;
- KPI and chart data use bounded, low-cardinality telemetry;
- manual time demonstrates expiry without wall-clock delay;
- sequential/parallel and bounded/uncontrolled comparisons are reproducible;
- scenario summaries distinguish business correctness from performance observations;
- the complete demo starts through one documented `dotnet run` command.

## Platform references

- [Create responses in ASP.NET Core Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0)
- [ASP.NET Core built-in metrics](https://learn.microsoft.com/en-us/aspnet/core/log-mon/metrics/built-in?view=aspnetcore-10.0)
- [ASP.NET Core metrics and OpenTelemetry](https://learn.microsoft.com/en-us/aspnet/core/log-mon/metrics/metrics?view=aspnetcore-10.0)

## Related documents

- [Purpose and value](01-purpose-and-value.md)
- [Proof-of-concept programme](02-proof-of-concept-programme.md)
- [Implementation plan](03-implementation-plan.md)
- [Production implementation status](04-production-status.md)
