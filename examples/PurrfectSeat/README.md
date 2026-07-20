# PurrfectSeat.com

PurrfectSeat.com is the NetCats example application: a .NET 10 Minimal API and static browser demo for a cat-themed ticket market. It includes a catalogue of shows and showtimes, each backed by its own pure DDD `Performance` aggregate. It uses snapshot/hydrate persistence, optimistic compare-and-swap, cold `Latent<T>` application workflows, cancellation-aware payment simulation, and a small live operations projection.

Run the API and both browser surfaces:

```shell
just examples-run
```

Open [the Box Office](http://localhost:5000/) and [The Catwalk](http://localhost:5000/control-room). The latter requires the `Demo` environment, which the Just recipe sets.

Useful commands:

```shell
just examples-build
just examples-test
just examples-wasm-run
just examples-wasm-publish
```

The implementation deliberately keeps the domain free from NetCats, ASP.NET Core, telemetry, and persistence dependencies. The repository persists `PerformanceSnapshot` values and always calls `Performance.Hydrate` when a unit of work loads an aggregate; it never holds a mutable aggregate as its backing store.

## Hackathon deployment

The standard API host supports the shared, multi-user demo. For a no-backend static deployment, use [PurrfectSeat.Wasm](src/PurrfectSeat.Wasm/README.md). It runs the same booking application in browser WebAssembly through an API-shaped bridge. Each browser receives its own in-memory demo state, which makes it ideal for a reliable hackathon demonstration but not a replacement for the shared API host.
