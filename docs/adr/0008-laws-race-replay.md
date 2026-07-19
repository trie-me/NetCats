# ADR 0008: Reusable laws and race replay

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

Laws can be ordinary reusable .NET APIs, independent of the repository's xUnit infrastructure, with deterministic race replay.

## Decision

Represent failures as named `LawFailure` values and provide throwing assertion helpers at the public boundary. Consume generated monad instances directly. Record deterministic binary scheduling choices as a compact trace that can be replayed without wall-clock timing.

## Evidence

The suite runs functor/monad laws for unary and binary generated instances, runtime laws for coldness, memoisation, finalizers, fiber cancellation, and virtual time, proves a known broken monad fails meaningfully, and replays a 128-step race trace.

## Consequences

Property generation and exhaustive schedule exploration are intentionally outside this slice; the API is suitable for adding them without coupling to xUnit.
