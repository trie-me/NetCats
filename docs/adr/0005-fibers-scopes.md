# ADR 0005: Fibers and structured scopes

- Status: POC accepted
- Date: 2026-07-19

## Hypothesis

Native tasks can back logical fibers while a lock-protected scope supplies structured ownership and atomic close/registration semantics.

## Decision

Create and register a fiber under the scope lock before starting it. Closing atomically rejects new children, requests cancellation, closes nested scopes, and joins the snapshot. Expose request-only and cancel-and-await operations separately. Detached fibers require an explicit constructor.

## Evidence

Tests cover close-through-cleanup, nested closure, sibling cancellation on failure, race loser cancellation, parallel fan-out/fan-in, registry cleanup, and rejection after closure.

## Consequences

Fiber identity is a GUID and has no thread affinity or custom synchronization context. The default child failure policy is sibling cancellation; richer supervision policies remain production API work.
