# Production implementation status

The production implementation is now separate from the POC solution in [`NetCats.slnx`](../NetCats.slnx). POC assemblies are not production dependencies.

The first promoted vertical slice provides:

- `NetCats.Core`: cold, repeatable, iterative `Latent<T>` execution; error recovery; outcomes; and generic kind/typeclass contracts.
- `NetCats.Runtime`: latched cancellation, masking/polling, protected finalizers, logical fibers, and structured scope shutdown.
- `NetCats.Generators`: the first production incremental generator contract for explicit kind carriers.
- `NetCats.Reactive`: optional BCL `Task` and `IObservable<T>` adapters with explicit cardinality policy.
- `NetCats.Testing`: deterministic `TimeProvider` support.

`NetCats.Integration.Tests` exercises those public APIs through loopback HTTP, real asynchronous file I/O, high fan-out worker shutdown, and observable cancellation. These tests are local and offline, but use the same native boundaries that application code uses.

The remaining production work is intentionally still staged: generator law registration/duplicate-witness diagnostics, explicit memoisation policies, nested scopes and scheduler queues, richer resource APIs, full law suites, and package/CI hardening. The POC implementations remain the reference while each of those pieces is promoted with a stable production contract.

Run the production suites with:

```shell
dotnet test NetCats.slnx
```
