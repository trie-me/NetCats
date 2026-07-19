# NetCats Proof-of-Concept Programme

## Purpose

The NetCats architecture is coherent, but several behaviours must be proven before the public API and package structure become expensive to change. This programme turns those uncertainties into bounded experiments with explicit acceptance criteria.

The POCs should be implemented as disposable or selectively reusable vertical slices. Their purpose is to produce decisions, measurements, and executable evidence—not premature production code.

## Programme rules

- Keep each POC independently runnable and documented.
- Record the tested runtime, SDK, build configuration, and benchmark environment.
- Prefer semantic tests over demonstrations that only compile.
- Include adversarial race and failure cases, not just success paths.
- Avoid stabilising public names until the relevant POC passes.
- Promote code into the implementation only after review against the acceptance criteria.
- Treat a failed POC as useful evidence and revise the design rather than hiding the result.

## Recommended execution order

```text
POC 1: generated kinds
          |
          v
POC 2: Latent algebra -----> POC 3: stack-safe interpreter
                                   |
                                   v
POC 4: cancellation/finalizers --> POC 5: fibers and scopes
                                           |
                                           v
POC 6: logical scheduler/time --> POC 7: native Task and Rx interop
                                           |
                                           v
POC 8: laws and deterministic races --> POC 9: performance/AOT
                                           |
                                           v
                                  API and architecture decision
```

POCs 1 and 2 can proceed in parallel conceptually, but the programme should converge on one representation before POCs 3–9 are treated as implementation foundations.

## POC 1: Attribute-generated higher-kinded encodings

### Question

Can a Roslyn incremental generator make `K<F, A>`-style programming practical for registered C# type constructors without unacceptable allocations, diagnostics, or IDE friction?

### Build

Create an attribute-driven generator supporting:

- a unary constructor such as `Latent<A>`;
- a binary constructor with one fixed parameter such as `Either<E, A>`;
- a third-party type that cannot be declared `partial`;
- generated witnesses such as `LatentK` and `EitherK<E>`;
- conversions between concrete types and `K<F, A>`;
- generated `Select`, `SelectMany`, `Map`, and `Bind` surfaces;
- generated instance registration for a small `Functor`/`Monad` hierarchy;
- generated law-suite discovery;
- analyzers for duplicate witnesses, unsupported arity, incorrect value-parameter position, and missing partial declarations.

Use `IIncrementalGenerator`. Generated output must be deterministic, inspectable, and free from runtime reflection.

### Experiments

1. Compile a generic function against `K<F, A>` and run it for two generated constructors.
2. Compare a partial-type implementation with a generated wrapper for a third-party type.
3. Measure boxing and allocation behaviour for class and struct carriers.
4. Test IDE completion, navigation to generated code, diagnostics, and incremental rebuild behaviour.
5. Test NativeAOT and trimming compatibility.

### Acceptance criteria

- One open generic emission covers all values of `A`; the generator does not enumerate closed application types.
- Unary and partially applied binary constructors work across assembly boundaries.
- Generated diagnostics identify invalid declarations at the attribute location.
- Ordinary concrete code does not require direct use of `K<F, A>`.
- Generic paths have documented and measured allocation behaviour.
- Incremental rebuilds do not regenerate unrelated constructors.
- A small law suite can discover and run against generated instances.

### Decision produced

Choose among partial augmentation, generated wrappers, or a hybrid model, and freeze the witness/attribute contract needed by the runtime.

## POC 2: Cold and repeatable `Latent<T>` algebra

### Question

Can `Latent<T>` provide clear cold, repeatable semantics while remaining natural in direct C# API usage and optional LINQ query syntax?

### Build

Implement the smallest useful algebra:

- `Pure`;
- delayed synchronous evaluation;
- delayed asynchronous evaluation;
- `Select`/`Map`;
- `SelectMany`/`Bind`;
- error capture and recovery;
- explicit execution through `RunAsync`;
- explicit memoisation as a separate operation.

The fundamental rule is that constructing or composing a `Latent<T>` performs no user work. Each non-memoised execution creates a fresh task-producing operation.

### Experiments

1. Prove that construction and composition do not invoke factories.
2. Execute the same value repeatedly and verify independent state and cancellation.
3. Compare direct API composition with equivalent LINQ query syntax.
4. Verify that synchronous exceptions thrown by factories become effect failures.
5. Define and test the difference between repeat, memoise-success, and memoise-outcome.

### Acceptance criteria

- Coldness and repeatability are observable in tests.
- `SelectMany` is the semantic bind operation, not a separate compatibility implementation.
- Direct API and query-expression forms are behaviourally identical.
- Execution returns native `Task<T>` unless a later performance POC justifies `ValueTask<T>`.
- Memoisation policy is explicit and never implied by ordinary construction.

### Decision produced

Freeze the minimal algebra, execution entry point, and memoisation vocabulary.

## POC 3: Stack-safe interpreter

### Question

Can NetCats evaluate arbitrarily deep `SelectMany` composition without consuming the CLR call stack or yielding excessively?

### Build

Represent `Latent<T>` as an instruction algebra with an iterative run loop. At minimum, support pure values, delayed evaluation, asynchronous suspension, bind, failure handling, and cancellation checkpoints.

Compare this with a simple nested-delegate implementation as a baseline.

### Experiments

1. Execute at least one million left-associated binds that complete synchronously.
2. Execute right-associated and mixed synchronous/asynchronous chains.
3. Introduce failures and recovery at different depths.
4. Measure fairness when one fiber performs long synchronous bind sequences.
5. Compare throughput and allocations with direct task-based baselines.

### Acceptance criteria

- Deep composition does not overflow the stack.
- The interpreter preserves exceptions and values exactly once.
- The run loop can yield after a configurable operation budget.
- Fairness yielding does not change observable program results.
- Performance is measured and sufficiently competitive to continue with the instruction-tree design.

### Decision produced

Select the internal instruction representation, continuation-stack strategy, and fairness checkpoint policy.

## POC 4: Cancellation signal, masking, and finalization

### Question

Can a latched eventing signal plus a `CancellationToken` projection reproduce precise request, masking, finalization, and termination behaviour?

### Build

Implement:

- a one-shot `CancelSignal`;
- synchronous `IsRequested` observation;
- an awaitable `WhenRequested` event;
- a `CancellationToken` projection for native API boundaries;
- mask depth and pending cancellation in the runtime context;
- an operation equivalent to `uncancelable` with a temporary restore/poll capability;
- `Guarantee` and `GuaranteeCase`;
- an explicit `Outcome<T>` model;
- finalizer-stack execution with a defined error policy.

Cancellation request, fiber termination, and finalizer completion must remain distinct signals.

### Experiments

Exercise cancellation:

- before execution starts;
- during synchronous evaluation;
- while awaiting a native task;
- during masked acquisition;
- during ordinary use;
- immediately before finalizer registration;
- while a finalizer is running;
- concurrently with successful completion;
- concurrently with failure;
- repeatedly from multiple requesters.

### Acceptance criteria

- Cancellation is latched and visible to late observers.
- A masked request becomes observable at the next restored boundary.
- Finalizers run exactly once in reverse registration order.
- Default cancellation does not abort protected finalizers.
- `CancelAsync` can wait for fiber termination rather than merely issue a request.
- Outcome and finalizer-error precedence are deterministic and documented.
- Native APIs receive the correct token in interruptible regions and a deliberate protected token policy in masked regions.

### Decision produced

Freeze the cancellation state machine, masking rules, exit cases, and finalizer failure policy.

## POC 5: Fibers and structured scopes

### Question

Can logical fibers backed by native tasks provide structured ownership without introducing thread affinity or replacing ordinary async execution?

### Build

Implement:

- `Fiber<T>` with join, cancellation request, and cancel-and-await;
- `FiberScope` with atomic child registration and removal;
- nested scopes;
- scope-close behaviour that cancels and joins remaining children;
- configurable child-failure propagation;
- race and parallel composition built on fibers;
- diagnostics for leaked or unobserved fibers in development builds.

### Experiments

1. Race fiber completion against scope closure and child registration.
2. Verify that a child cannot become unowned between creation and scheduling.
3. Test nested-scope cancellation and failure propagation.
4. Run large fan-out/fan-in workloads.
5. Await external tasks within fibers and verify that native async behaviour remains intact.

### Acceptance criteria

- Every started fiber has a scope owner or is explicitly marked detached.
- Scope close waits until owned children and their finalizers terminate.
- No race permits an untracked child to escape a closing scope.
- Fiber identity is independent of whichever thread happens to execute a continuation.
- External task continuations do not require a custom `SynchronizationContext`.

### Decision produced

Freeze fiber lifecycle, scope ownership, detachment policy, and failure propagation defaults.

## POC 6: Logical scheduler and virtual time

### Question

Can a scheduler local to one `Latent` execution tree provide fairness and deterministic testing without harming native `Task` scheduling?

### Build

Define a logical scheduling abstraction for:

- enqueueing runnable fiber continuations;
- yielding after a configurable run-loop budget;
- ordering logical work;
- delays and timers through `TimeProvider`;
- production and deterministic test modes;
- optional projection to an Rx scheduler.

Do not initially subclass `TaskScheduler` or install a global `SynchronizationContext`.

### Experiments

1. Run multiple CPU-active logical fibers and verify bounded fairness.
2. Park fibers on native asynchronous tasks and re-enter the logical scheduler on completion.
3. Advance fake time deterministically through delay, timeout, retry, and race operations.
4. Verify that unrelated application tasks are unaffected.
5. Identify which external task completions cannot be deterministically ordered and document the boundary.

### Acceptance criteria

- Scheduler state is isolated to an execution tree.
- Native task operations continue to use normal .NET execution facilities.
- A busy logical fiber cannot starve its siblings indefinitely.
- Time-based NetCats primitives are deterministic under a fake `TimeProvider`.
- Production use requires no process-wide scheduler mutation.

### Decision produced

Freeze the scheduler/runtime boundary, fairness budget, and virtual-time testing contract.

## POC 7: Native Task, disposal, and Reactive Extensions interoperability

### Question

Can NetCats interoperate bidirectionally with existing .NET asynchronous APIs without weakening ownership, cancellation, or finalization guarantees?

### Build

Provide experimental adapters for:

- `Task<T>` and task-producing functions;
- `ValueTask<T>` where safe;
- `CancellationToken`-accepting APIs;
- `IAsyncDisposable` and `await using`;
- `IObservable<T>` through Reactive Extensions;
- optionally `IAsyncEnumerable<T>` as a streaming boundary.

### Experiments

1. Confirm that task factories remain latent while already-started tasks are documented as eager imports.
2. Map task success, fault, and cancellation into `Outcome<T>`.
3. Verify asynchronous disposal under success, failure, and cancellation.
4. Project `Latent<T>` to a single-result observable and define subscription repeatability.
5. Import observables under explicit zero/one/many policies.
6. Verify that subscription disposal requests cancellation and that separate completion can be awaited.

### Acceptance criteria

- APIs distinguish task factories from already-running tasks.
- Token ownership and linking are explicit.
- Async disposal is awaited and participates in finalizer policy.
- Rx projection does not pretend that synchronous subscription disposal means fiber termination.
- Zero/one/many observable semantics are explicit at conversion sites.
- No adapter silently changes repeatability or memoisation.

### Decision produced

Freeze the interop contracts and decide whether Rx is a core dependency, an optional package, or only an adapter target.

## POC 8: Laws and deterministic race testing

### Question

Can the laws be encoded as reusable .NET APIs and executed deterministically across generated instances and runtime implementations?

### Build

Create reusable suites covering:

- functor and monad laws;
- error and cancellation identities;
- guarantee/finalizer laws;
- resource acquisition and release laws;
- fiber join/cancel laws;
- race winner/loser cleanup;
- scheduler fairness invariants;
- memoisation semantics.

Integrate generator-emitted registrations and a deterministic scheduler/time provider.

### Experiments

1. Run laws against `Latent<T>` and at least one simpler reference implementation.
2. Introduce known faulty implementations and confirm that laws fail meaningfully.
3. Replay race schedules from a stored seed or execution trace.
4. Measure test-suite duration and flakiness over repeated runs.

### Acceptance criteria

- Law failures identify the violated rule and generated instance.
- Race failures are replayable.
- The suite contains no wall-clock sleeps.
- Known cancellation and finalizer defects are detected.
- Library authors can invoke the suites without internal NetCats test infrastructure.

### Decision produced

Freeze the public law-testing surface and the compatibility bar for new instances.

## POC 9: Performance, allocation, diagnostics, and deployment

### Question

Is the complete design viable for production .NET workloads and deployment modes?

### Build

Create representative benchmarks and sample applications covering:

- synchronous bind-heavy composition;
- asynchronous I/O composition;
- fiber fan-out/fan-in;
- cancellation storms;
- resource-heavy workflows;
- generated generic code;
- Task and Rx adapters;
- tracing enabled and disabled;
- JIT, trimming, and NativeAOT builds.

### Experiments

Compare against direct idiomatic `Task` implementations. Measure throughput, tail latency, allocations, retained objects, generated assembly size, startup, and cancellation/finalizer latency.

### Acceptance criteria

- Performance costs are quantified by feature rather than hidden in aggregate results.
- The no-tracing production path avoids unnecessary metadata allocation.
- Generated code is trimming-safe and NativeAOT-compatible, or limitations are explicit.
- No benchmark reveals unbounded queue, continuation, or scope-registry growth.
- The team agrees that the semantic value justifies the measured overhead.

### Decision produced

Set performance budgets, choose `Task` versus selective `ValueTask` boundaries, and approve or revise the architecture for production implementation.

## Deliverables from every POC

Each POC should leave five artefacts:

1. a concise design note stating the hypothesis;
2. runnable code isolated from production packages;
3. automated semantic tests;
4. measurements where performance or allocation is relevant;
5. an architecture decision record: accept, revise, or reject.

## Programme exit gate

Production implementation should begin only when POCs 2–6 demonstrate a coherent runtime and no POC has an unresolved correctness blocker. POCs 1, 7, and 8 must pass before committing to the public generic and interoperability surfaces. POC 9 must pass before declaring the first production release candidate.

## Related documents

- [Purpose and value](01-purpose-and-value.md)
- [Implementation plan](03-implementation-plan.md)
