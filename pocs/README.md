# NetCats proof-of-concept solution

`NetCats.Pocs.slnx` contains one project for each of the nine experiments, an additional Roslyn incremental-generator project for POC 1, and one test project for all validations.

| POC | Project | Executable evidence |
| --- | --- | --- |
| 1 | `NetCats.Poc01.GeneratedKinds` + `NetCats.Poc01.Generator` | Generated unary/binary witnesses, third-party wrapper, syntax, instances, registry, diagnostics, allocation probe |
| 2 | `NetCats.Poc02.Latent` | Cold/repeatable algebra, LINQ, recovery, explicit memoisation policies |
| 3 | `NetCats.Poc03.Interpreter` | Iterative instruction interpreter, fairness budget, delegate baseline |
| 4 | `NetCats.Poc04.Cancellation` | Latched signal, masking/poll, outcomes, protected LIFO finalizers |
| 5 | `NetCats.Poc05.Fibers` | Fibers, atomic scope ownership, nesting, failure propagation, race/parallel |
| 6 | `NetCats.Poc06.Scheduling` | Execution-local operation budgets, `TimeProvider`, manual deterministic time |
| 7 | `NetCats.Poc07.Interop` | Task/ValueTask, async disposal, and dependency-free `IObservable<T>` adapters |
| 8 | `NetCats.Poc08.Laws` | Reusable laws and seed/trace race replay |
| 9 | `NetCats.Poc09.Performance` | Executable per-feature timing/allocation and deployment report |

## Run

From the repository root:

```shell
dotnet test pocs/NetCats.Pocs.Tests/NetCats.Pocs.Tests.csproj
dotnet run --project pocs/NetCats.Poc09.Performance/NetCats.Poc09.Performance.csproj
dotnet publish pocs/NetCats.Poc09.Performance/NetCats.Poc09.Performance.csproj -c Release -r osx-arm64 --self-contained true -p:PublishAot=true
```

The first command is the single validation entry point. The performance executable is intentionally a lightweight probe rather than a statistically rigorous BenchmarkDotNet suite; it makes costs visible while the representations are still disposable.

## Recorded environment and baseline

The implementation was validated on 2026-07-19 with .NET SDK 10.0.300, .NET runtime 10.0.8, and macOS arm64. One illustrative Debug/JIT run at 10,000 bind operations measured roughly 20 ns/op and 72 B/op for the direct-task loop versus 165 ns/op and 218 B/op for the instruction-tree path. A Release NativeAOT publish produced and directly executed a 64-bit arm64 Mach-O binary; it reported dynamic code as unsupported/uncompiled and completed every representative probe. These numbers are environmental observations, not performance budgets; rerun the executable on target hardware before making a production decision.

The provisional decisions and remaining limitations are in [`docs/adr/`](../docs/adr/).
