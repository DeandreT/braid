# Worker producer accounting for the native CSP fixture

Apply `worker-lifecycle.patch` to the Chromium pin in [README.md](README.md), after `native-phase.patch`. The worker patch touches worker, worklet, and service-worker registration files only. It uses `Scope` and `ExecutionContextCSPDelegate::SetFixtureProbe(scope, check)` from the phase patch and the existing `EventTarget` at-target hook. It adds no Inspector command or driver fixture and leaves normal CSP behavior unchanged.

## Qualification boundary

- A classic dedicated worker reserves an identity on its creator's sequence before `DedicatedWorker::Start` sends `CreateWorkerHost`. The reservation transfers once via `GlobalScopeCreationParams` into `WorkerThread::Start`, before posting thread initialization. A canceled, rejected or dropped handoff cannot retire as a delivery. Nested classic dedicated workers use the same process ledger and get independent identities.
- `WorkerOrWorkletGlobalScope::BindContentSecurityPolicyToExecutionContext` installs the worker's delegate before binding its inside-settings CSP. The callback gets the reservation ID and real `WorkerToken`. The phase patch owns the credit from before posting the violation through the exact original event and receiver, settling it only after dispatch returns. A confirmed before-handler observation is **not** a transport receipt.
- `WorkerGlobalScope::RunWorkerScript` resolves startup only after successful top-level evaluation. `WorkerThread::PrepareForShutdownOnWorkerThread` seals new CSP work before lifecycle observers and scheduler disposal. `PerformShutdownOnWorkerThread` checks after global scope and backing-thread disposal and before the termination notification can delete the `WorkerThread`. Pending, dropped, or late dispatch, failed startup, and forced disposal refuse. A replacement can never retire the old identity.
- The renderer locally refuses module, shared, service and unknown worker starts, and worklets. Worklets do not dispatch DOM CSP events in the delegate. Startup/outside-settings CSP uses a different delegate; its forwarding paths may drop events or dispatch on another context. Any such report observed in the renderer refuses instead of transferring credit. These local hooks cannot establish absence of out-of-process or pre-existing producers, browser-side startup checks, or browser-initiated service-worker restarts.

## Combined runtime integration

1. The trusted fixture owner must call `blink::InstallWorkerCspFixture(check)` once in each admitted renderer, before worker/worklet ingress. Installation after any covered ingress, a second installation, or a missing callback refuses. Use a fresh isolated renderer and bind admission to the exact fixture/run, not a public runtime option or a target-attach notification. If the owner cannot establish the renderer and producer inventory, refuse at the browser level.
2. In the callback map `{run, renderer, reservation ID, WorkerToken}` to the original generation and record the exact trusted native event using the paired receipt mechanism. The callback runs on the worker sequence before handlers; use thread-safe state, don't retain the event, and don't read or mutate document Inspector state from the worker thread. A callback must not let a later generation acknowledge an earlier one. The owner must separately prove dispatch completion and journal/transport acknowledgement for each recorded event.
3. Fence real browser ingress, require disposal of all admitted worker identities, and include `SealAndCheckTerminal()` for every enrolled renderer in the terminal check. A non-`kNone` result refuses. An empty local ledger is **not** a cutoff. Renderer loss, target detach, unadmitted or unidentified producers, out-of-process startup/restart, and missing, unacknowledged or late receipts must be handled by the combined owner. Do not infer them from this local check.

For the combined worker control, use a separate intercepted fixture response that allows its exact classic worker script. Put `connect-src 'none'` on the worker response, not on the document, and require the original `WorkerToken` in the receipt. Keep service workers blocked in the fresh fixture context. A check of the existing document-only `worker-src 'none'` fixture cannot substitute for this control.

## Focused checks

After both patches are applied, run from the Chromium source root, with `FIXTURE` set to the absolute path of this directory and `OUTPUT` set to a task-owned build directory:

```sh
third_party/llvm-build/Release+Asserts/bin/clang++ \
  -std=c++20 -pthread -Wall -Wextra -Werror -I . \
  "$FIXTURE/worker-lifecycle-test.cc" -o "$OUTPUT/worker-lifecycle-test"
timeout 10 "$OUTPUT/worker-lifecycle-test"
```

The standalone binary exercises the same lifecycle ledger with a real thread handoff and simulated credits. In the combined native build, use the bounded `blink_unittests` procedure in the fixture README with `--gtest_filter=WorkerThreadTest.FixtureCsp*`. The two tests use running Blink workers and a `connect-src` violation without a network request. One verifies at-target dispatch on the original worker and subsequent graceful retirement; the other closes the worker with dispatch still queued and expects refusal. Neither exercises the driver receipt.
