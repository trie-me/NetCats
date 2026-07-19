# NetCats: Purpose and Value

## Executive summary

NetCats aims to bring the practical guarantees of Cats Effect to .NET without attempting a line-by-line translation of Scala APIs or syntax. It will provide a small, lawful effect model for describing asynchronous work, composing it before execution, managing resources, coordinating concurrent fibers, and making cancellation behaviour explicit and testable.

.NET already has excellent execution primitives: `Task`, `async`/`await`, `CancellationToken`, `IAsyncDisposable`, `TimeProvider`, the thread pool, and Reactive Extensions. NetCats should build on those primitives. Its purpose is to supply the semantic layer that they do not provide as a unified whole: cold and repeatable effect descriptions, structured ownership, stack-safe composition, explicit outcomes, cancellation masking, reliable finalization, and laws that can be checked across implementations.

The project should therefore be understood as a .NET-native interpretation of Cats Effect, not a Scala compatibility layer.

## The problem NetCats addresses

Idiomatic .NET asynchronous code is effective locally, but system-wide guarantees are usually assembled through convention. A `Task<T>` represents an execution that may already have started. A `CancellationToken` announces a cooperative request but does not mean the operation or its cleanup has finished. `await using` provides excellent lexical disposal but does not itself model cancellation masking, exit cases, dynamic resource graphs, or structured child ownership. Detached tasks can outlive the operation that created them unless the application maintains its own supervision discipline.

These are not failures of the .NET platform. They reflect the platform's role as an execution environment rather than a single opinionated effect system. NetCats will add an optional composition model for applications that need stronger guarantees.

## Why Cats Effect is valuable in .NET

### Effects become values

NetCats will represent an asynchronous computation as a cold value, provisionally named `Latent<T>`. Constructing a `Latent<T>` will not start work. Each execution will create fresh runtime state unless memoisation is requested explicitly.

This separation makes programs easier to reason about:

- construction describes what should happen;
- composition describes dependencies and policies;
- execution decides when and under which scope the work happens;
- tests can inspect behaviour under controlled cancellation, time, and scheduling.

### Composition has laws

.NET already contains the correct vocabulary for monadic composition:

- `Select` is map;
- `SelectMany` is bind.

NetCats will support direct API composition as the primary style while remaining naturally compatible with LINQ query syntax. Law suites will verify identity, associativity, cancellation, resource, and concurrency behaviour rather than relying only on example-based tests.

### Cancellation becomes a protocol

In ordinary task-based code, cancellation is commonly treated as a token check and an `OperationCanceledException`. NetCats will treat it as a protocol with distinct stages:

1. cancellation is requested;
2. the request becomes observable at a permitted boundary;
3. the fiber begins cancellation;
4. finalizers run in protected regions;
5. termination becomes observable to joiners.

A latched cancellation signal will provide asynchronous notification, while a `CancellationToken` projection will preserve synchronous observation and interoperability at .NET API boundaries.

### Resource safety composes

`IAsyncDisposable` and `await using` will remain the preferred .NET interop surface. NetCats will add compositional operations equivalent to acquire/use/release, guarantee, and guarantee-with-exit-case. Finalizer ordering, masking, and error precedence will be defined by the effect model and verified by laws.

### Concurrency is structured

NetCats fibers will be logical computations backed by native tasks. NetCats will not create, own, or assign physical threads to fibers; continuations will execute through the ordinary .NET async runtime. A scope will own its child fibers, determine failure propagation, and cancel and join outstanding work when the scope closes.

A lightweight logical scheduler will coordinate runnable fibers within a single `Latent` execution tree. It will provide fairness, testing hooks, and virtual-time integration without replacing the native task scheduler. When a fiber awaits a native task, .NET remains responsible for the underlying asynchronous operation.

### Generic functional APIs become practical

C# does not directly express higher-kinded types such as `F<A>` where `F` is a type constructor parameter. NetCats will investigate an attribute-driven Roslyn incremental generator that emits concrete witnesses, conversions, instances, syntax, and law registrations for declared type constructors.

The intended result is compile-time generation with good IDE discoverability and no runtime reflection. A small `K<F, A>` representation may remain internally or at advanced generic boundaries, while ordinary users work with concrete types such as `Latent<T>`.

### Time and concurrency become deterministic in tests

The runtime will use `TimeProvider` for clocks and delays and will expose a deterministic logical scheduler for law and race testing. This will allow tests to advance time, explore cancellation boundaries, and verify finalizer ordering without depending on wall-clock sleeps.

## Design principles

1. **Native first.** Use `Task`, `async`/`await`, `CancellationToken`, `IAsyncDisposable`, `TimeProvider`, and standard diagnostics wherever they already express the required behaviour.
2. **Semantics over syntax parity.** Preserve guarantees, not Scala spelling or operator density.
3. **Cold by default.** Descriptions do not run until explicitly started, and repeated starts create repeated executions unless memoised.
4. **API-first composition.** `Select`, `SelectMany`, and named combinators are first-class APIs; LINQ query syntax is supported naturally but not required.
5. **Logical fibers, native tasks.** Fibers add ownership and cancellation semantics while native tasks perform asynchronous execution.
6. **A scheduler per execution tree.** Logical scheduling coordinates only the fibers owned by a NetCats runtime scope and does not replace global task scheduling.
7. **Cancellation is observable and awaitable.** Request, observation, cleanup, and termination are distinct.
8. **Finalizers are protected.** Cleanup runs under explicit masking and has deterministic ordering and failure semantics.
9. **Generation is additive and explicit.** Type constructors opt in through attributes; generated code is inspectable and analyzable.
10. **Laws are part of the product.** Behavioural laws and deterministic runtime tests define compatibility more strongly than API shape alone.

## Proposed conceptual model

```text
Attribute-driven generator
        |
        v
Type constructors, K<F,A>, instances, syntax and laws

Latent<T> --start--> Fiber<T> --executes through--> Task<T>
    |                    |
    |                    +--> Outcome<T>
    |                    +--> CancelSignal
    |                    +--> FiberScope
    |
    +--> Resource<T> / finalizer stack
    +--> logical scheduler / TimeProvider
    +--> Task, Rx and IAsyncDisposable interoperability
```

The names are provisional, but the responsibilities should remain separate.

## Explicit non-goals

NetCats will not:

- replace `Task` or `async`/`await`;
- create or own threads for normal effect or fiber execution;
- replace the global .NET task scheduler;
- reproduce Scala infix syntax;
- require LINQ query expressions;
- force functional abstractions on code that is adequately served by direct task-based APIs;
- provide unsafe termination of arbitrary blocking code;
- port every Cats or Cats Effect API before the runtime semantics are proven.

## Who benefits

NetCats is most valuable for .NET systems where correctness depends on composition across multiple asynchronous boundaries:

- services with complex resource lifetimes;
- concurrent workflows with child-task ownership requirements;
- resilient clients with retries, races, timeouts, and cancellation;
- libraries that need generic effect capabilities;
- applications requiring deterministic asynchronous tests;
- teams that want enforceable functional laws rather than informal conventions.

## Success criteria

NetCats succeeds if it can demonstrate that:

- `Latent<T>` composition is stack-safe and lawful;
- cold execution and explicit memoisation are unambiguous;
- cancellation races produce deterministic outcomes;
- finalizers run exactly once in the defined order;
- canceling a fiber can be awaited through finalization;
- scoped fibers cannot leak unnoticed;
- native tasks and async APIs interoperate without losing their normal strengths;
- generated higher-kinded encodings are usable, diagnosable, and sufficiently allocation-efficient;
- virtual-time law tests execute deterministically;
- the API feels like modern C#, even where the semantics are inspired by Cats Effect.

## Related documents

- [Proof-of-concept programme](02-proof-of-concept-programme.md)
- [Implementation plan](03-implementation-plan.md)
