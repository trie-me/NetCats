# PurrfectSeat.Wasm

`PurrfectSeat.Wasm` is a standalone .NET WebAssembly browser-service hackathon demo. It retains the existing JavaScript Box Office and runs the same Domain, Application, Infrastructure, and NetCats projects in the browser. `WasmBookingBridge` exposes `BrowserBookingApi` through supported .NET `[JSExport]` calls.

Browsers cannot bind an HTTP listener, so this project does not run Kestrel or claim to provide a public HTTP server from WebAssembly. Instead, the JavaScript API client calls the browser-local bridge for the same booking operations—catalogue, availability, hold, confirm, and cancel. This makes it suitable for static deployment and a reliable no-backend demo. Each browser session has an independent in-memory catalogue.

For a shared multi-user experience, deploy the existing `PurrfectSeat.Api` host. For a static hackathon demonstration, run or publish this project:

```shell
just examples-wasm-run
just examples-wasm-publish
```

The local development host prints its chosen loopback URL when it starts, avoiding conflicts with the API demo or other local services.

The static publish output is in `bin/Release/net10.0/publish/wwwroot`. Deploy its contents to any static-file host with a navigation fallback to `index.html`.
