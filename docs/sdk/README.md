# MutualGPU provider SDK

The MutualGPU provider SDK lets Node.js and Chrome applications enroll an execution unit, receive assignments, execute provider-owned workloads, and publish results without constructing the provider protocol directly.

These are the only supported consumer environments in the current preview:

| Environment | Packages | Connection |
| --- | --- | --- |
| Node.js | `@mutualgpu/provider-core` and `@mutualgpu/provider-node` | Native HTTPS/HTTP2 gRPC |
| Chrome | `@mutualgpu/provider-core` and `@mutualgpu/provider-web` | Binary Protobuf over secure WebSocket |

The current packages are repository-local preview packages. They are ECMAScript modules and do not yet ship TypeScript declaration files or a documented public-registry distribution channel.

## Start here

- [Provider SDK usage guide](guides/mutualgpu-provider-sdk.md) — end-to-end enrollment, assignment, input, result, failure, reconnect, and shutdown workflow.
- [Consumer API reference](reference/provider-api.md) — constructors, methods, task fields, state rules, errors, and environment differences.
- [Enrollment schema reference](reference/enrollment-schema.md) — machine, capability, input, and output definitions with validation constraints.

## Consumer boundary

Provider applications describe and run workloads. The SDK owns authentication headers, protocol envelopes, task handles, progress sequence numbers, upload authorization, result multipart construction, checksums, completion receipts, reconnect, and active-task rebinding.

Consumers must protect the provider key, validate requestor-supplied inputs, enforce their own workload timeouts and cancellation, produce declared result parts, and close the provider during graceful shutdown.

The low-level Protobuf codec and transport protocol are implementation details unless a reference page explicitly marks a member as part of the consumer API.
