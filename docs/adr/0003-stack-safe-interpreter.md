# ADR 0003: Iterative instruction interpreter

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

An instruction tree plus an explicit continuation stack can execute deeply associated binds without consuming the CLR stack.

## Decision

Use instruction nodes for pure, delay, async suspension, failure, bind, and recovery. Evaluate them with one iterative loop and explicit bind/error frames. Yield through `Task.Yield` after a configurable operation budget.

## Evidence

The shared suite executes one million left-associated binds, 100,000 right-associated binds, mixed sync/async chains, deep failure recovery, and cancellation checkpoints. POC 9 compares the path with a direct-task loop.

## Consequences

The representation allocates nodes and continuation frames. Current measurements justify continued semantic work, not a final performance budget; node specialization and pooled/segmented stacks remain candidates.
