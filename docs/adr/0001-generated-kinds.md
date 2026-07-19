# ADR 0001: Generated kind representation

- Status: POC accepted provisionally
- Date: 2026-07-19

## Hypothesis

An incremental generator can keep `K<F, A>` at generic boundaries while concrete C# code uses normal carrier types and generated LINQ/direct-method surfaces.

## Decision

Use a hybrid contract: partial first-party carriers receive generated witnesses, conversions, monad instances, and syntax; non-partial third-party types are represented by an explicit generated-surface wrapper. Generation is compile-time only and the law registry contains instance objects, so discovery uses no reflection.

## Evidence

The POC generates unary `LatentK`, partially applied `EitherK<E>`, and `ThirdPartyBoxK`. Tests compile generic functions for two witnesses, inspect deterministic generated output, run generated law registrations, measure class/struct carrier allocation, and compile invalid declarations to verify `NCGEN001`–`NCGEN004`.

## Consequences

`K<F, A>` currently stores `object`, so struct carriers box on generic paths. Concrete paths avoid `K`. The generated code passed AOT compatibility analysis and was included in the successfully executed macOS arm64 NativeAOT POC host; other target RIDs still need a release matrix.
