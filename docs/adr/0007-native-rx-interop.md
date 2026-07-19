# ADR 0007: Native and observable interoperability

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

Adapters can preserve coldness, cancellation, and cleanup if eager imports and stream cardinality are explicit.

## Decision

Name task-factory and already-started-task imports separately. Await `IAsyncDisposable` cleanup in `finally`. Keep Rx optional by targeting the BCL `IObservable<T>` interfaces. Observable imports require `ExactlyOne`, `First`, or `Last`; projected subscriptions expose a separate completion task because synchronous disposal only requests cancellation.

## Evidence

Tests cover task success/fault/cancellation outcomes, factory repeatability versus eager imports, async disposal on all exits, observable repeatability, zero/one/many policy, and disposal-versus-termination.

## Consequences

No `System.Reactive` dependency is required by the POC. Production scheduler-specific adapters can live in an optional package.
