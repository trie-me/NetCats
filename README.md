# NetCats

**NetCats is a native reimplementation of Cats Effect semantics for .NET.** It brings cold effects, structured concurrency, explicit cancellation, protected finalization, and deterministic testing to modern C# while continuing to use the platform’s own `Task`, `async`/`await`, `CancellationToken`, `IAsyncDisposable`, and `TimeProvider` primitives.

It is not a Scala compatibility layer or a replacement for .NET asynchronous programming. Think of it as an optional semantic layer for workflows where it must be clear when work starts, who owns concurrent work, what cancellation means, and when cleanup is actually complete.

## Getting started

Prerequisites:

- .NET 10 SDK
- [`just`](https://github.com/casey/just) — optional, but recommended for the short commands below

From the repository root, validate the production implementation:

```shell
dotnet restore NetCats.slnx
dotnet test NetCats.slnx
```

Or use the project recipes:

```shell
just check          # build and test production plus the example
just pocs           # run the POC semantic validation suite
just examples-run   # start the PurrfectSeat demo in Demo mode
```

When the example is running, open:

- Box Office: <http://localhost:5000/>
- The Catwalk operator dashboard: <http://localhost:5000/control-room>

Use `Ctrl+C` to stop the host. Run `just --list` to see every recipe.

## Why NetCats?

`Task<T>` is excellent for executing asynchronous work, but it usually represents work that has already started. Larger systems often need more explicit, composable guarantees:

| Concern | Native .NET gives you | NetCats adds |
|---|---|---|
| Starting work | `Task<T>` / `async` starts naturally | Cold `Latent<T>` descriptions that run only through `RunAsync` |
| Cancellation | Cooperative request via `CancellationToken` | Observable request, protected cleanup, and a terminal `Outcome<T>` |
| Child work | Tasks must be supervised by convention | `FiberScope` ownership, cancellation, and join-on-close |
| Cleanup | `using` / `await using` are lexical | Protected finalizer execution with defined exit behaviour |
| Asynchronous tests | Good platform primitives, often wall-clock driven | Deterministic time support and testable lifecycle boundaries |
| Functional abstractions | No first-class higher-kinded types | Explicit carrier contracts and an opt-in source generator |

NetCats is most useful in services and libraries with resource lifetimes, retry/timeout policies, concurrent child workflows, or business operations that must remain predictable under cancellation and failure. For a straightforward single `await`, ordinary .NET code is often the best choice.

## Core ideas

```csharp
var effect = Latent<int>.Delay(() => 21)
    .Map(value => value * 2);

// Nothing has run above.
var answer = await effect.RunAsync(); // 42
```

- **`Latent<T>`** — a cold, repeatable description of asynchronous work. `Select`, `SelectMany`, `Bind`, and recovery compose it without starting it.
- **`Outcome<T>`** — explicit success, cancellation, or fault outcome for joined fibers.
- **`Fiber<T>` and `FiberScope`** — logical asynchronous work backed by native tasks. A scope owns its children and closes only after cancellation and deregistration complete.
- **Cancellation and finalization** — `CancelSignal`, `CancellationContext`, `FinalizerStack`, and `ProtectedExecution` make request, cleanup, and termination separate, observable stages.
- **Generated kinds** — an incremental Roslyn generator supports explicit generic kind-carrier declarations without runtime reflection.
- **Interop** — adapters preserve normal .NET `Task` and `IObservable<T>` use at system boundaries.

Read the [development guide](docs/06-development-guide.md) for an end-to-end Minimal API pattern and design guidance.

## Project layout

| Location | Purpose |
|---|---|
| [NetCats.slnx](NetCats.slnx) | Production Core, Runtime, Generator, Reactive, and Testing projects plus their tests |
| [pocs/](pocs/README.md) | Nine isolated semantic proof-of-concepts and one validation test project |
| [examples/PurrfectSeat/](examples/PurrfectSeat/README.md) | Runnable .NET 10 DDD Minimal API, Box Office, Catwalk dashboard, and example tests |
| [docs/](docs/) | Design rationale, ADRs, status, implementation specification, and guides |
| [justfile](justfile) | Common build, test, POC, and demo commands |

## PurrfectSeat.com example

PurrfectSeat.com demonstrates NetCats in an ordinary business application rather than a toy console program. It is a cat-themed concert-ticket marketplace with multiple shows and showtimes, a snapshot/hydrate DDD repository, optimistic concurrency, cancellation-aware payment simulation, availability SSE, and an operator dashboard.

The domain model stays free of NetCats and ASP.NET Core. The application layer returns cold `Latent<T>` workflows; Minimal API handlers execute them with the request token and map expected business outcomes to explicit typed HTTP results.

```shell
just examples-run
```

See the [example README](examples/PurrfectSeat/README.md) and [implementation specification](docs/05-purrfectseat-example-implementation.md).

For a static hackathon deployment with no backend, [PurrfectSeat.Wasm](examples/PurrfectSeat/src/PurrfectSeat.Wasm/README.md) runs the same domain and application services in browser WebAssembly. It uses an API-shaped in-process bridge because a browser cannot host a network listener. Each browser session has its own in-memory catalogue; use the API host for a shared multi-user demonstration.

## Current status

The production solution is a deliberately promoted first slice, not a claim that every planned Cats Effect-style API is complete. It currently includes cold iterative effects, error recovery, cancellation masking, protected finalization, logical fibers and scopes, an initial generator, Task/Rx adapters, and deterministic `TimeProvider` support.

The remaining staged work includes richer compositional resource APIs, broader concurrency/scheduler primitives, full law suites, additional generator diagnostics, memoisation policies, and package/CI hardening. The POCs remain isolated reference experiments while those contracts mature.

For detail, see [production implementation status](docs/04-production-status.md) and the [proof-of-concept programme](docs/02-proof-of-concept-programme.md).

## Testing

Tests are layered from pure semantic and domain checks through local, real-boundary integration tests using loopback HTTP, asynchronous file I/O, fiber shutdown, and observable cancellation.

```shell
just test
just examples-test
just pocs
```

The [testing guide](docs/07-testing-guide.md) explains what belongs at each layer and how to test coldness, cancellation, finalization, DDD workflows, and concurrent races without timing guesses.

## Documentation

- [Purpose and value](docs/01-purpose-and-value.md)
- [Proof-of-concept programme](docs/02-proof-of-concept-programme.md)
- [Implementation plan](docs/03-implementation-plan.md)
- [Production implementation status](docs/04-production-status.md)
- [PurrfectSeat.com implementation specification](docs/05-purrfectseat-example-implementation.md)
- [Development guide](docs/06-development-guide.md)
- [Testing guide](docs/07-testing-guide.md)
- [Architecture decisions](docs/adr/)

## License

NetCats is licensed under the [GNU Affero General Public License v3.0](LICENSE) (`AGPL-3.0-only`).
