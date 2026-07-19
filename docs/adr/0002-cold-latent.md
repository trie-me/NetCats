# ADR 0002: Cold `Latent<T>` vocabulary

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

A small factory-backed algebra can make construction cold, normal execution repeatable, and LINQ equivalent to direct composition.

## Decision

Retain `Pure`, `Delay`, `DelayAsync`, `Select`/`Map`, `SelectMany`/`Bind`, recovery, and `RunAsync`. `SelectMany` is the single bind implementation. Normal execution repeats; `MemoizeSuccess` retries failed/cancelled attempts, while `MemoizeOutcome` caches the first terminal task.

## Evidence

Tests cover construction coldness, repeat state, independent cancellation, LINQ equivalence, synchronous exception capture, recovery, and both memoisation policies.

## Consequences

Execution returns `Task<T>`. A memoized computation intentionally shares the cancellation token of the attempt that populates its cache; a production API should document or refine multi-caller cancellation ownership.
