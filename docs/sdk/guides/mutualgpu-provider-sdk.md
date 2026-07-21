# MutualGPU provider SDK usage guide

The MutualGPU provider SDK lets an application advertise the work it can perform and handle tasks from the exchange. Provider code describes capabilities, decides whether to accept a task, executes the workload, and produces a result. The SDK owns the API protocol around those decisions.

The current demo SDK supports Node.js and Chrome providers. The Swift package is not part of the current demo release.

## What the SDK handles

Provider code does not construct MutualGPU API requests. The SDK handles:

- provider authentication;
- enrollment serialization and server-owned capability identity;
- connection setup and protocol negotiation;
- assignment acknowledgement and task authorization;
- progress throttling;
- input URL refresh;
- result authorization, checksums, upload, and completion;
- reconnect and active-task rebinding.

Provider code is responsible for:

- describing the machine and capabilities it offers;
- accepting or rejecting each assignment;
- validating and executing requestor-supplied inputs safely;
- producing a valid ZIP and any declared optional outputs;
- reporting execution failure when appropriate;
- closing the client during graceful shutdown.

## Packages

Use `@mutualgpu/provider-core` with the transport for the provider environment. Core and transport packages should use the same SDK version.

| Package | Purpose |
| --- | --- |
| `@mutualgpu/provider-core` | `ProviderClient` and the task lifecycle |
| `@mutualgpu/provider-node` | Node.js transport |
| `@mutualgpu/provider-web` | Chrome transport |

## Configuration

An SDK consumer supplies two values:

- the MutualGPU HTTPS base URL;
- the provider preshared key issued by the MutualGPU operator.

The SDK derives the required service endpoints from the base URL. It rejects cleartext URLs before sending credentials.

The provider key identifies one configured execution unit. It is not a requestor credential or a storage credential. Keep it out of source control, generally accessible browser bundles, and logs.

## Provider lifecycle

A provider has one lifecycle:

1. Construct a transport and `ProviderClient`.
2. Enroll the machine and its capabilities.
3. Connect a task handler.
4. Accept or reject each assignment.
5. After acceptance, execute work and optionally report progress.
6. Upload the result and complete with the returned receipt, or fail the task.
7. Close the provider during shutdown.

`ProviderClient` permits one active task. Use one client per execution unit instead of adding application-side concurrency around a single client.

## Describe the provider

Enrollment describes the machine and the complete set of capabilities currently offered by that execution unit:

```js
const definition = {
  machine: {
    tier: "Large",
    specifications: {
      computeTier: "Large",
      memoryGiB: 32
    }
  },
  capabilities: [{
    name: "example-renderer",
    description: "Produces a rendered asset bundle.",
    inputs: [{
      key: "quality",
      type: "Integer",
      required: false,
      label: "Quality",
      default: "80",
      minimum: 1,
      maximum: 100,
      displayOrder: 0
    }],
    output: {
      hasMetadata: true,
      hasLogs: true
    }
  }]
};
```

Capability identity and continuity fields are server-owned. The SDK supplies them; consumers should not create capability IDs or contract hashes.

`machine.tier` is the T-shirt scheduling abstraction. Its concrete values are `Small`, `Medium`, `Large`, and `ExtraLarge`; `Automatic` is only a requestor choice and is invalid during enrollment.

`machine.specifications` describes the enrolled hardware for the supplemental availability matrix. `computeTier` supplies the CPU/GPU row and `memoryGiB` supplies the numeric memory column. These specifications do not become task constraints and do not replace the single T-shirt tier selector.

### Inputs

Each input has a stable `key` used in `task.scalars`. Supported types are:

```text
String, Integer, Number, Boolean, Date, DateTime, DateTimeOffset, Image
```

An enrollment can declare at most one image input. Image definitions can restrict accepted content types. Scalar definitions can declare defaults, ranges, or allowed values where appropriate.

### Outputs

Every successful task produces a ZIP. A capability can additionally declare:

- metadata;
- a thumbnail;
- a preview and its accepted content types;
- UTF-8 logs.

Only upload optional parts declared by the capability.

## Node.js provider

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiUrl = new URL(process.env.MUTUALGPU_API_URL);
const providerKey = process.env.MUTUALGPU_PROVIDER_KEY;

const provider = new ProviderClient(
  new NodeGrpcTransport(apiUrl, providerKey)
);

await provider.enroll(definition);

await provider.connect(async task => {
  if (!canRun(task)) {
    await task.reject("Required local runtime is unavailable");
    return;
  }

  await task.accept();

  await task.reportProgress({
    phase: "render",
    percent: 10,
    message: "Starting renderer"
  });

  const input = task.input ? await downloadInput(task.input) : undefined;
  const resultZip = await render({ scalars: task.scalars, input });

  const { receipt } = await task.uploadResult({
    resultZip,
    metadata: { renderer: "example-renderer" },
    logs: "Render completed successfully.\n"
  });

  await task.complete(receipt);
});
```

The provider session remains active after `connect` completes its handshake. The underlying connection keeps the Node.js process available for assignments.

## Chrome provider

Chrome uses the same lifecycle with its browser transport:

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { BrowserWebSocketTransport } from "@mutualgpu/provider-web";

const provider = new ProviderClient(
  new BrowserWebSocketTransport("https://api.example/", providerKey)
);

await provider.enroll(definition);
await provider.connect(async task => {
  await task.accept();
  const resultZip = await runWebGpuWork(task.scalars);
  const { receipt } = await task.uploadResult({ resultZip });
  await task.complete(receipt);
});
```

The MutualGPU operator must allow the provider application's browser origin. That is deployment configuration; SDK code still needs only the API base URL and provider key.

Do not ship a provider key in a generally accessible public web application. A Chrome provider is suitable only when the provider operator controls the browser environment and key distribution.

## Accept or reject an assignment

An assignment starts pending. Before doing expensive work, call exactly one of:

```js
await task.accept();
```

or:

```js
await task.reject("GPU is below the required capability");
```

Receiving an assignment is not acceptance. If the handler returns while pending, the SDK rejects it automatically. After acceptance, the handler must complete or fail the task.

## Read task inputs

Scalar values are available as strings:

```js
const quality = Number(task.scalars.quality ?? "80");
```

An image assignment can contain an opaque, short-lived HTTPS descriptor:

```js
async function downloadInput(descriptor) {
  const response = await fetch(descriptor.url);
  if (!response.ok) throw new Error(`Input download failed: ${response.status}`);
  return new Uint8Array(await response.arrayBuffer());
}
```

If the URL expires after acceptance, request a replacement through the task object:

```js
const refreshedUrl = await task.refreshInputDownload();
```

Fetch the URL as supplied. The storage implementation behind it is not part of the SDK contract.

## Report progress

Progress is optional and is valid only after acceptance:

```js
const sent = await task.reportProgress({
  phase: "inference",
  percent: 42,
  message: "Processing tile 21 of 50"
});
```

The SDK sends at most one update per second. It returns `false` when a call is coalesced, so execution must not depend on every progress update being delivered.

## Publish a result

Use `task.uploadResult`; do not construct an upload request:

```js
const { receipt, sha256 } = await task.uploadResult({
  resultZip,
  metadata: { durationMs: 812 },
  thumbnail,
  preview,
  logs
});

await task.complete(receipt);
```

`uploadResult` obtains authorization, calculates the ZIP checksum, builds and sends the result, validates the response, and returns the completion receipt. The returned `sha256` is available for application diagnostics.

Current demo limits are:

| Value | Constraint |
| --- | --- |
| `resultZip` or `zip` | Required valid ZIP, at most 50 MiB |
| `metadata` | JSON object, at most 64 KiB |
| `thumbnail` | Valid PNG, JPEG, or WebP, at most 5 MiB |
| `preview` | Declared image content type, at most 5 MiB |
| `logs` | UTF-8 text, at most 1 MiB |

The complete result is limited to 64 MiB. A malformed result ends that attempt; application code should not replay the same upload. MutualGPU owns the task's retry policy.

## Report failure

After acceptance, report an execution failure with a stable step name and safe diagnostic message:

```js
if (!modelFilesAreValid) {
  await task.fail("model_load", "Installed model files failed validation");
  return;
}
```

Do not include provider keys, input URLs, requestor data, or other secrets in failure reasons.

An exception that escapes the handler after acceptance is converted by `ProviderClient` into an `execution` failure. Call `task.fail` directly when the application has a more useful stable failure step.

## Reconnect and shutdown

The SDK automatically retries an unexpected connection loss with bounded backoff. If a task is active, it attempts to rebind that task during the server's grace period. Application code should keep the active workload in place and must not start duplicate execution.

If rebind is no longer authorized, treat the old attempt as lost. Do not reuse old result authorization or try to complete it outside the SDK lifecycle.

During graceful shutdown:

```js
process.on("SIGTERM", () => provider.close());
process.on("SIGINT", () => provider.close());
```

Finish or fail accepted work before closing when possible. `close()` stops reconnect and closes the provider session.

## Error handling

Errors from SDK methods mean the lifecycle operation did not complete. Useful handling rules are:

- enrollment failure: correct the capability definition or provider configuration before reconnecting;
- authentication failure: stop and replace the provider key or base URL;
- assignment rejection: return normally after `task.reject`;
- workload failure after acceptance: call `task.fail` once;
- result validation failure: do not replay the upload;
- connection loss: allow automatic reconnect unless shutting down.

`ProviderUploadError` describes a rejected result. `ProviderClientError` identifies invalid local lifecycle operations, such as reporting progress before acceptance.

## Local integration

From the repository root, trust the local certificate once and run the SDK handshake against the real API:

```text
just mutualgpu-dev-cert
just mutualgpu-local-smoke
```

The smoke command enrolls through the SDK, opens a provider session, waits for connection, and exits. It performs no GPU work.

Run a long-lived simulated provider for manual task submission:

```text
just mutualgpu-local-demo
```

Open `https://localhost:7043`, submit a task to `mutualgpu-local-demo`, and observe the SDK accept, report progress, upload a synthetic ZIP, and complete it.

Run the retained API, frontend, and SDK tests with:

```text
just mutualgpu-test
```

## Consumer checklist

- Use matching core and transport package versions.
- Supply an HTTPS base URL.
- Keep the provider key outside source control and logs.
- Enroll before connecting.
- Explicitly accept or reject every assignment.
- Keep one active task per `ProviderClient`.
- Use task methods instead of constructing API calls.
- Upload only result parts declared during enrollment.
- Complete with the receipt returned by `uploadResult`.
- Let the SDK own reconnect and task rebinding.
- Close the provider during graceful shutdown.
