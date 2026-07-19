# NetCats Implementation Plan

## Objective

Implement a production-quality, .NET-native effect system inspired by Cats Effect. NetCats will describe cold asynchronous computations, compose them lawfully, execute them through native tasks, protect resources, structure concurrency, and provide deterministic testing without replacing the .NET async runtime.

This plan begins after the relevant proof-of-concept gates in the [POC programme](02-proof-of-concept-programme.md) have passed. POC code may be promoted selectively, but the production implementation should be organised around stable responsibilities rather than the shape of the experiments.

## Current architectural decisions

The following decisions are the working baseline:

- The primary effect type is provisionally named `Latent<T>`.
- Constructing or composing a `Latent<T>` is cold; each normal execution is fresh.
- `Select` is map and `SelectMany` is bind in the native .NET LINQ model.
- Named `Map` and `Bind` aliases may exist, but must delegate to the same semantics.
- Direct API composition is first-class; LINQ query expressions are optional syntax.
- Fibers are logical computations backed by native tasks; execution remains the responsibility of the ordinary .NET async runtime.
- The library follows `async`/`await` and does not replace the global task scheduler.
- A logical scheduler coordinates only the fibers in a given execution tree.
- The logical scheduler owns queues and scope coordination, never physical threads.
- A scope, not the scheduler, owns child fibers.
- Cancellation uses a latched eventing signal with a `CancellationToken` projection.
- Cancellation request, cleanup completion, and fiber termination are distinct.
- `IAsyncDisposable` and `await using` are primary resource interop mechanisms.
- `TimeProvider` is the clock and timer abstraction.
- Reactive Extensions may provide coordination and streaming adapters but will not automatically become the single-result effect representation.
- Higher-kinded and typeclass surfaces will be generated from explicit attributes where the POC validates the design.
- Laws and deterministic race tests form part of the compatibility contract.

Names, packaging, and low-level representations remain provisional until their POCs pass.

## Target architecture

```text
                                  +----------------------+
                                  | NetCats.Generators   |
                                  | witnesses, instances |
                                  | syntax, diagnostics  |
                                  +----------+-----------+
                                             |
                                             v
+------------------+              +----------+-----------+
| User application| composition  | NetCats.Core         |
| and libraries   +------------->| Latent, Outcome, K   |
+--------+---------+              | Resource, laws       |
         |                        +----------+-----------+
         | execute                           |
         v                                   v
+--------+-----------------------------------+-----------+
| NetCats.Runtime                                       |
| interpreter | CancelSignal | Fiber | FiberScope       |
| logical scheduler | finalizer stack | tracing         |
+----------+----------------+---------------------------+
           |                |
           v                v
   Native Task APIs     TimeProvider
   async/await          timers and virtual time
           |
           v
   Optional adapters: Rx, IAsyncEnumerable, hosting/testing
```

## Proposed repository and package structure

The exact solution layout should be established in Phase 0. A likely package split is:

```text
src/
  NetCats.Core/                 foundational public abstractions
  NetCats.Runtime/              interpreter and production runtime
  NetCats.Generators/           incremental source generator
  NetCats.Analyzers/            diagnostics not owned by generation
  NetCats.Reactive/             optional Reactive Extensions adapters
  NetCats.Testing/              laws, deterministic runtime, test helpers

tests/
  NetCats.Core.Tests/
  NetCats.Runtime.Tests/
  NetCats.Generators.Tests/
  NetCats.Reactive.Tests/
  NetCats.Laws.Tests/
  NetCats.Integration.Tests/

benchmarks/
  NetCats.Benchmarks/

samples/
  NetCats.Samples.Basic/
  NetCats.Samples.Concurrency/
  NetCats.Samples.Resources/

pocs/
  ...                           retained experiments and decision evidence

docs/
  ...                           design, POCs, implementation, ADRs
```

Keep `NetCats.Core` dependency-light. Rx must remain optional unless the interop POC proves that it is necessary to the core runtime.

## Initial public API sketch

The sketch exists to align implementation work; it is not yet a compatibility promise.

```csharp
public readonly struct Latent<T>
{
    public Latent<TResult> Select<TResult>(
        Func<T, TResult> selector);

    public Latent<TResult> SelectMany<TResult>(
        Func<T, Latent<TResult>> selector);

    public Latent<TResult> SelectMany<TIntermediate, TResult>(
        Func<T, Latent<TIntermediate>> selector,
        Func<T, TIntermediate, TResult> projector);

    public Latent<TResult> Map<TResult>(
        Func<T, TResult> selector);

    public Latent<TResult> Bind<TResult>(
        Func<T, Latent<TResult>> selector);

    public Task<T> RunAsync(
        CancellationToken cancellationToken = default);
}
```

Runtime and concurrency concepts:

```csharp
public abstract record Outcome<T>
{
    public sealed record Succeeded(T Value) : Outcome<T>;
    public sealed record Faulted(Exception Error) : Outcome<T>;
    public sealed record Cancelled : Outcome<T>;
}

public interface IFiber<T>
{
    Task<Outcome<T>> JoinAsync();
    Task CancelAsync();
}

public interface ICancelSignal
{
    bool IsRequested { get; }
    CancellationToken Token { get; }
    Task WhenRequested { get; }
}
```

The exact representation of `Outcome<T>`, allocation strategy, and task/value-task choices must follow POC measurements.

## Phase 0: Repository and engineering foundation

### Work

- Create the solution, projects, central package management, and build configurations.
- Adopt repository conventions, editor configuration, warnings, nullable annotations, formatting, and static analysis.
- Establish target frameworks based on supported runtime requirements such as `TimeProvider`.
- Configure deterministic builds, source-linking, package metadata, and API compatibility checks.
- Add unit, integration, generator snapshot, law, and benchmark test projects.
- Establish CI for supported operating systems and target frameworks.
- Add an architecture-decision-record directory and template.
- Define versioning and experimental API policies.

### Exit criteria

- A clean checkout builds and tests with one documented command.
- Formatting and warnings are enforced consistently.
- Packages can be produced locally with deterministic metadata.
- CI exercises the same commands used by contributors.

## Phase 1: Generated kind and instance foundation

### Dependencies

POC 1 accepted.

### Work

- Define the minimal `K<F, A>` and witness contracts.
- Define attributes for unary and partially applied constructors.
- Implement the incremental generator pipeline.
- Generate concrete bridges, witnesses, instance declarations, and optional syntax.
- Implement diagnostics for invalid, duplicate, or ambiguous declarations.
- Add generator snapshot tests and compile-and-run integration tests.
- Document advanced generic use separately from ordinary concrete use.

### Exit criteria

- Unary and selected multi-parameter constructors generate correctly.
- Cross-assembly consumption works.
- Generated code is deterministic, incremental, trimming-safe, and diagnosable.
- Law registration can be generated without runtime reflection.

## Phase 2: Core `Latent<T>` algebra and interpreter

### Dependencies

POCs 2 and 3 accepted.

### Work

- Implement the internal instruction representation.
- Implement an iterative interpreter and continuation stack.
- Add pure, delay, asynchronous suspension, failure, recovery, and bind nodes.
- Implement `Select`, `SelectMany`, `Map`, and `Bind` through one semantic path.
- Add cold execution factories and explicit already-running-task import APIs.
- Define repeat, memoisation, and caching operations distinctly.
- Capture synchronous factory exceptions as effect failures.
- Add fairness checkpoints without introducing the full scheduler prematurely.
- Implement structural tracing hooks that can be disabled cheaply.

### Exit criteria

- Monad laws pass.
- Million-bind stress tests remain stack-safe.
- Construction is proven cold and normal execution repeatable.
- API and LINQ composition are equivalent.
- Baseline allocation and throughput budgets are recorded.

## Phase 3: Outcomes, cancellation, and resource safety

### Dependencies

POC 4 accepted.

### Work

- Implement `Outcome<T>` and exit-case conversion.
- Implement `CancelSignal` with synchronous state, awaitable request, and token projection.
- Add runtime mask depth, pending request tracking, and cancellation checkpoints.
- Implement uncancelable regions and temporary restoration/polling.
- Implement guarantee, guarantee-with-exit-case, bracket, and resource composition.
- Implement the finalizer stack and reverse-order execution.
- Define finalizer-error combination and precedence.
- Add `IDisposable` and `IAsyncDisposable` acquisition adapters.
- Ensure cancellation of an execution can await protected cleanup.

### Exit criteria

- Cancellation/finalizer state-machine tests pass under deterministic race control.
- Every registered finalizer executes at most once and required finalizers execute exactly once.
- Masking and restoration behaviour is documented through examples and laws.
- Resource laws pass for success, failure, and cancellation.

## Phase 4: Fibers, scopes, and logical scheduling

### Dependencies

POCs 5 and 6 accepted.

### Work

- Implement logical fiber identity and lifecycle.
- Implement join, request cancellation, and cancel-and-await.
- Implement `FiberScope` with atomic registration/removal.
- Implement nested scopes and explicit detached-fiber policy.
- Implement scope-close cancellation and join.
- Add a scheduler local to each runtime execution tree.
- Add operation-budget fairness and explicit yielding.
- Integrate `TimeProvider` for sleep and timer operations.
- Provide production and deterministic test scheduler implementations.
- Ensure parked native tasks resume into the correct logical execution context.
- Add fiber-local context if justified; do not equate it with thread-local state.

### Exit criteria

- Scope registration/closure races cannot leak owned fibers.
- Busy logical fibers yield fairly.
- Native tasks continue to use normal .NET scheduling facilities.
- No global scheduler or synchronization-context mutation is required.
- Time-based tests run without wall-clock delays.

## Phase 5: Concurrent and temporal operations

### Work

Build higher-level operations from the proven fiber and scheduler primitives:

- start/fork;
- race and race-pair;
- parallel tuple and traversal;
- timeout and timeout-to;
- sleep;
- retry policies;
- bounded parallelism;
- supervisor/scoped background execution;
- deferred one-shot coordination;
- semaphore and queue primitives if required by target use cases.

Avoid porting the full Cats Effect surface indiscriminately. Each operation must have a clear .NET use case and laws or state-machine tests.

### Exit criteria

- Losers of races are canceled and finalized according to policy.
- Bounded operations remain bounded under failure and cancellation.
- Temporal operations use the runtime `TimeProvider` exclusively.
- Concurrency combinators contain no unowned background tasks.

## Phase 6: Native .NET and Rx interoperability

### Dependencies

POC 7 accepted.

### Work

- Finalise task-factory, `Task<T>`, and `ValueTask<T>` adapters.
- Distinguish latent task creation from importing already-running tasks.
- Finalise token-linking and ownership rules.
- Add `IAsyncDisposable` resource helpers.
- Add hosting and dependency-injection integration only where concrete use cases require it.
- Implement Rx adapters in `NetCats.Reactive`.
- Define zero/one/many policies when importing observables.
- Optionally add `IAsyncEnumerable<T>` adapters without conflating streams and single-result effects.

### Exit criteria

- Adapter behaviour is documented for coldness, repetition, cancellation, and ownership.
- Rx subscription disposal and fiber termination remain observably distinct.
- Core packages do not depend on Rx unless an explicit architecture decision changes this.
- Integration tests cover representative BCL and third-party async APIs.

## Phase 7: Laws and testing product

### Dependencies

POC 8 accepted.

### Work

- Publish reusable functor, monad, cancellation, resource, fiber, and concurrency laws.
- Integrate generator-produced instance discovery.
- Implement deterministic scheduler traces and replay.
- Add property-based input generation where it improves coverage.
- Provide testing helpers for advancing time, forcing boundaries, inspecting scopes, and asserting outcomes.
- Keep test-only introspection out of normal runtime surfaces.

### Exit criteria

- New typeclass/effect implementations can consume the law suites externally.
- Known broken implementations fail with actionable diagnostics.
- Race failures are replayable from a seed or trace.
- The full deterministic suite is stable under repeated CI execution.

## Phase 8: Diagnostics, performance, and hardening

### Dependencies

POC 9 accepted or incorporated as this phase's entry gate.

### Work

- Add opt-in fiber dumps, scope trees, cancellation traces, and finalizer traces.
- Integrate with `ActivitySource`, `EventSource`, or metrics only after measuring overhead.
- Benchmark direct tasks against representative NetCats programs.
- Optimise instruction representation, continuation storage, and scheduler queues.
- Evaluate selective `ValueTask` use only where consumption rules remain safe.
- Audit unbounded growth, disposal, token-source lifetime, and exception retention.
- Validate trimming and NativeAOT.
- Run stress, soak, cancellation-storm, and high-fan-out tests.
- Conduct public API review and compatibility analysis.

### Exit criteria

- Performance budgets are met or consciously revised.
- Tracing-disabled overhead is understood and acceptable.
- No known scope, continuation, finalizer, or cancellation leaks remain.
- Deployment limitations are explicit.
- Public API review approves the first release-candidate surface.

## Phase 9: Documentation, samples, and release

### Work

- Write a conceptual guide explaining effects, execution, fibers, scopes, and resources in .NET terms.
- Publish API examples using direct methods first and LINQ syntax second.
- Provide migration examples from direct task workflows where NetCats adds value.
- Document when not to use NetCats.
- Publish resource, cancellation, and concurrency recipes.
- Document generated attributes, diagnostics, and advanced `K<F, A>` usage.
- Provide benchmark methodology and semantic compatibility statements.
- Produce signed packages and release notes.

### Exit criteria

- A new user can build a resource-safe concurrent program without understanding Scala.
- Advanced users can write generic lawful code using generated kinds.
- Every public cancellation and ownership operation has documented semantics.
- The release candidate passes CI, laws, stress tests, package validation, and sample execution.

## Cross-cutting implementation requirements

### Correctness

- All mutable runtime transitions use explicit state machines and race tests.
- Cancellation and completion paths use idempotent transitions.
- Finalizer registration and scope registration are atomic with respect to cancellation/closure.
- Exceptions are never silently dropped.
- Detached work is explicit and diagnosable.

### Performance

- Optimise only after measuring representative programs.
- Avoid boxing in hot generic paths where the generator can emit a specialised bridge.
- Avoid allocating trace metadata when tracing is disabled.
- Keep scheduler queues bounded by runnable work and release completed fiber state promptly.
- Prefer `Task<T>` for clarity until selective `ValueTask<T>` use is proven beneficial.

### Compatibility

- Follow standard async naming and cancellation conventions at .NET boundaries.
- Do not capture application `SynchronizationContext` unintentionally.
- Do not require a custom global `TaskScheduler`.
- Keep optional integrations in separate packages.
- Treat generated source as a public debugging surface: readable, deterministic, and documented.

### Testing

- Unit-test pure algebra and state transitions.
- Property-test laws.
- Deterministically test runtime schedules and virtual time.
- Integration-test real task, disposal, and Rx boundaries.
- Stress-test high contention and cancellation.
- Benchmark release builds separately from correctness tests.

## Key risks and mitigations

### Generated higher-kinded APIs remain awkward

**Mitigation:** Keep concrete APIs primary, confine `K<F, A>` to advanced generic boundaries, generate conversions, and ship analyzers with actionable diagnostics.

### The interpreter adds unacceptable overhead

**Mitigation:** Retain direct task baselines, benchmark each semantic feature, specialise common nodes, and consider tiered execution paths only after correctness is stable.

### Cancellation races create ambiguous behaviour

**Mitigation:** Specify a small state machine before implementation, separate request from termination, and require deterministic replay tests for every transition.

### The logical scheduler fights native async

**Mitigation:** Schedule only NetCats run-loop work, never replace global task scheduling, and treat external task completion ordering as an explicit interop boundary.

### Rx introduces mismatched stream semantics

**Mitigation:** Keep Rx optional, require explicit cardinality policies, and distinguish subscription disposal from awaited fiber termination.

### Scope tracking retains completed work

**Mitigation:** Remove children promptly on terminal transition, use stress tests and memory profiling, and expose development diagnostics for retained scopes.

## Release strategy

Use capability-based previews rather than claiming Cats Effect completeness:

1. **Experimental core:** cold `Latent<T>`, lawful composition, stack-safe interpreter.
2. **Resource preview:** cancellation, masking, finalizers, resource composition.
3. **Concurrency preview:** fibers, scopes, scheduler, time, races.
4. **Interop preview:** Task, disposal, Rx, testing packages.
5. **Release candidate:** generated kinds, complete law suites, diagnostics, hardening.

Each preview may change APIs. The first stable release should promise semantic behaviour only after the law, race, and performance gates have passed.

## Definition of done

The initial feature is complete when NetCats provides:

- a cold and repeatable `Latent<T>`;
- stack-safe `SelectMany` composition;
- native task execution;
- explicit success, failure, and cancellation outcomes;
- a latched cancellation protocol with masking;
- protected, ordered asynchronous finalization;
- logical fibers and structured scopes;
- a scheduler local to each execution tree;
- virtual time through `TimeProvider`;
- generated higher-kinded/typeclass support for declared constructors;
- optional Rx interoperability;
- reusable laws and deterministic race tests;
- acceptable measured overhead and production diagnostics;
- documentation that explains the model entirely in .NET terms.

## Related documents

- [Purpose and value](01-purpose-and-value.md)
- [Proof-of-concept programme](02-proof-of-concept-programme.md)
