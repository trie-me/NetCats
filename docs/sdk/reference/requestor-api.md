# Requestor browser API

Use `@mutualgpu/requestor-web` from browser applications that discover capabilities, submit work, poll task state, or retrieve result descriptors.

```js
import { RequestorClient } from "@mutualgpu/requestor-web";

const requestor = new RequestorClient("https://mutualgpu.com/");
const capabilities = await requestor.listCapabilities();
```

Before the first API operation, the client performs one credentialed `GET /` and waits for the anonymous requestor cookie. Concurrent initial operations share that bootstrap request. Every API request uses `credentials: "include"`; if the API still reports `requestor_identity_missing`, the client retries that operation once.

## Methods

| Method | Endpoint |
| --- | --- |
| `listCapabilities()` | `GET /api/capabilities/` |
| `listTasks()` | `GET /api/tasks/` |
| `getTask(taskId)` | `GET /api/tasks/{taskId}` |
| `submitTask(submission, image?)` | `POST /api/tasks/` |
| `reevaluateTask(taskId)` | `POST /api/tasks/{taskId}/reevaluate` |
| `getTaskResult(taskId)` | `GET /api/tasks/{taskId}/result` |
| `createWebGpuEnrollment()` | `POST /api/webgpu-enrollments` |

`submitTask` sends JSON when `image` is omitted and multipart form data when passed a `Blob` or `File`. Keep scalar values as strings and echo the current capability contract hash.

Failures throw `RequestorApiError` with `status`, `code`, `problem`, and raw `body` properties. Result artifact download URLs are short-lived; request a fresh result descriptor after expiry.

Cross-origin applications must be exact entries in `MutualGPU:ProviderCorsOrigins`. The API cookie is `HttpOnly; Secure; SameSite=None`, so application JavaScript never reads or copies it.
