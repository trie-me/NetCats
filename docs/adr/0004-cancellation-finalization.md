# ADR 0004: Cancellation and finalization protocol

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

A latched request signal, mask depth, and protected finalizer stack can distinguish request, observation, cleanup, and termination.

## Decision

Use a one-shot `CancelSignal` with synchronous state, awaitable notification, and token projection. Masked native boundaries receive `CancellationToken.None`; `Poll` temporarily restores the prior depth. Finalizers run once in LIFO order and continue after individual failures. Operation plus finalizer failures become an ordered `AggregateException`; finalizer failure turns success/cancellation into failure.

## Evidence

Tests cover late and concurrent requesters, masked pending cancellation, restored polling, protected reverse-order finalization, and deterministic error precedence.

## Consequences

The mutable mask depth is local to one logical fiber and must not be used concurrently. Production code should encode that ownership explicitly in its runtime context.
