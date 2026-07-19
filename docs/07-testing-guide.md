# Testing guide

## Testing philosophy

NetCats tests behaviour at the boundary where a guarantee matters. A green asynchronous test is not enough if it fails to prove whether work was cold, whether cancellation completed cleanup, or whether child work escaped its owner.

Use the smallest test that proves the contract, then add a real-boundary test for any important adapter. Avoid using arbitrary `Task.Delay` values as a substitute for a synchronization point.

## Fast commands

Run these from the repository root:

```shell
just test             # production NetCats tests
just pocs             # all POC semantic validations
just examples-test    # PurrfectSeat domain, application, API-surface, and scenario tests
just check            # production plus example build and tests
```

The underlying commands use `--disable-build-servers`; this keeps local test execution reliable in restricted or short-lived development environments.

## Test layers

| Layer | What it proves | Examples in this repository |
|---|---|---|
| Unit / semantic | Pure state transitions and effect semantics | `Latent<T>` coldness, recovery, stack safety; aggregate hold transitions |
| Runtime | Cancellation, finalizer ordering, structured ownership | `FiberScope` drain behaviour and `ProtectedExecution` finalization |
| Native integration | Behaviour across actual .NET boundaries | Loopback `HttpClient`, async `FileStream`, observable disposal, cancellation of a real request |
| Application | Workflow outcomes and persistence decisions | A hold is cold until run; decline leaves an active hold; snapshot/hydrate persistence |
| Scenario | Invariants under contention or repeated workflow composition | Contended PurrfectSeat holds cannot oversell a seat |
| API surface | Typed handler contracts and published assets | Named typed endpoint result and Box Office/Catwalk static assets |

The production integration tests are deliberately offline. Loopback HTTP and local asynchronous file I/O provide real native behaviour without an external database, cloud account, or flaky network dependency.

## Testing `Latent<T>`

Test construction separately from execution:

```csharp
var calls = 0;
var effect = Latent<int>.Delay(() => ++calls);

Assert.Equal(0, calls);             // constructed, not run
Assert.Equal(1, await effect.RunAsync());
Assert.Equal(1, calls);
```

For composition, include a deep-bind test. The interpreter is intended to be iterative, so a deeply composed effect should complete without a stack overflow. For failure handling, assert both the recovered value and that unrelated errors are not accidentally swallowed.

Do not use `FromStartedTask` to assert coldness: it deliberately wraps work that is already started. Prefer `DelayAsync` for a new operation on each run.

## Testing cancellation and finalization

Cancellation has stages: request, observation, cleanup, and termination. Tests should synchronize with an observable boundary, request cancellation, then await the resulting fiber or scope.

```csharp
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var scope = new FiberScope();
var fiber = scope.Start(Latent<int>.DelayAsync(async token =>
{
    entered.TrySetResult();
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return 1;
}));

await entered.Task;
await scope.CloseAsync();

Assert.IsType<Outcome<int>.Cancelled>(await fiber.JoinAsync());
Assert.Equal(0, scope.ActiveChildCount);
```

For resource tests, register a real finalizer and then prove the resource is available after termination. The production suite does this with an exclusive async `FileStream`. Test finalizer order and failure precedence separately; a finalizer should run once even when the main action fails or is cancelled.

## Testing a DDD application workflow

Keep pure aggregate tests free of NetCats. They should assert state transitions, versions, domain events, and invalid transitions directly.

Application tests should use real application ports with deterministic fakes or simulators:

1. Seed an immutable aggregate snapshot.
2. Construct the application workflow without running it; assert the store has not changed.
3. Run the `Latent<T>` once.
4. Load through a new unit of work and assert the aggregate was hydrated from its stored snapshot.
5. Assert the business result and the invariant.

For PurrfectSeat, the minimum useful cases are:

- an available seat becomes held and receives an expiry;
- two active holds cannot allocate the same seat;
- an expired or cancelled hold cannot be confirmed;
- a payment decline leaves the hold active;
- an optimistic-version loss retries only while the seats might still be available;
- a contended-seat scenario never oversells.

## Time, retries, and races

Prefer `NetCats.Testing.ManualTimeProvider` whenever the code under test can accept a `TimeProvider`. Advance manual time to trigger expiry or timeout instead of waiting for wall-clock time.

For races, replace timing guesses with barriers:

- `TaskCompletionSource` marks that a worker has reached a precise point;
- a deterministic fake port blocks authorisation, save, or publish until the test releases it;
- after releasing both paths, assert exactly one conditional save wins;
- await all fibers/scopes before asserting no work remains.

Seed any random simulator and include the seed in a failing assertion message. A scenario that cannot be replayed is a demonstration, not a dependable test.

## Failure diagnostics

When an async test fails, record enough state to distinguish an application bug from a test race:

- `Outcome<T>` for every joined fiber;
- active child count after scope closure;
- version and seat/hold states before and after a compare-and-swap;
- cancellation request time and finalizer completion time;
- scenario seed, concurrency limit, and selected seat set.

Do not assert implementation details such as thread IDs. Fibers are logical computations and are intentionally not thread ownership claims.

## Before opening a change

Run the most specific suite while developing, then finish with:

```shell
just check
```

If a change affects cancellation, fibers, finalization, time, or adapters, add a focused semantic test and at least one integration test at the relevant native boundary. If it changes a business workflow, add a domain or application test and a scenario invariant where contention or retries are involved.
