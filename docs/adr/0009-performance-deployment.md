# ADR 0009: Performance and deployment viability

- Status: POC accepted for continued iteration; budgets not frozen
- Date: 2026-07-19

## Hypothesis

The semantic design's overhead can be attributed to individual features and checked for deployment compatibility before production APIs are frozen.

## Decision

Keep `Task<T>` as the execution boundary. Retain a standalone executable probe for direct tasks, interpreted binds, generated kinds, fibers, cancellation, finalizers, observable projection, and tracing. Record elapsed time and total allocations per feature. Enable AOT compatibility analysis on the generated-kind runtime slice.

## Evidence

On the recorded Debug/JIT environment, a 10,000-operation sample measured about 20 ns/op and 72 B/op for a direct task loop versus 165 ns/op and 218 B/op for instruction-tree binds. The shared tests verify every probe completes, uses bounded operation counts, and reports nonnegative allocation/time data. A Release `osx-arm64` NativeAOT publish completed without AOT/trimming warnings, produced a Mach-O arm64 executable, and ran every probe directly with dynamic code reported as disabled.

## Consequences

These lightweight figures are directional, not statistically rigorous. Release BenchmarkDotNet runs, retained-object profiling, and NativeAOT publishing across the remaining target RIDs are required before setting production budgets.
