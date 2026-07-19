# ADR 0006: Execution-local scheduling and time

- Status: POC accepted with boundary documented
- Date: 2026-07-19

## Hypothesis

Fairness checkpoints and time can be local to an execution tree without replacing `TaskScheduler` or installing a synchronization context.

## Decision

Give each logical runtime an operation budget and yield natively when it is exhausted. Route temporal primitives through an execution-tree `TimeProvider`. Use `ManualTimeProvider` for deterministic timers and expose an optional scheduler projection without taking an Rx dependency.

## Evidence

Tests verify exact yield budgets across concurrent logical runtimes, deterministic delay/cancellation, scheduler projection, and isolation of clock state.

## Consequences

External task completion ordering remains controlled by .NET and is not deterministic. The scheduler only makes NetCats checkpoints and timers deterministic.
