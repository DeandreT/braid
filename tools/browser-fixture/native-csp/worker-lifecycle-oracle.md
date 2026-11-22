# Worker lifecycle and worklet CSP oracle

This is a defensive behavior control for **stock** Linux x64 Chromium
`157.0.8090.0`, revision `2ea1b71af70d7fc5d41325dda8df37c1f98ac531`. It is
not a native producer ledger. All documents and scripts are synthetic
intercepted `.invalid` responses. HTTPS is provided by interception so
that Paint Worklet runs in a secure context; no traffic or server is needed.
The sandbox and normal browser flags are unchanged.

## Cases

- **Classic worker, inside settings.** The document permits same-origin
  connections, but both classic worker HTTP responses set `connect-src
'none'`. Each worker announces readiness, and then its own harmless fetch
  is blocked before reaching interception. Its CSP listener verifies the
  trusted event's target and current target are that worker global, with
  the distinct fixture ID and script URL. Tracing correlates each listener
  mark with that worker's initialized thread, not the document's thread.
  The owner sees only its separate image CSP event.
- **Terminate and recreate.** The original worker reports its own
  violation first. The runner calls `terminate()` and waits for the
  matching CDP `Target.targetDestroyed` and Playwright `close` before
  creating the replacement. The replacement reports a different fixture
  ID, request URL, and CDP target ID. None is a `WorkerToken` or proof of
  native disposal, scheduler drain, or receipt. It does not test a
  pending-event/termination race or service-worker restart.
- **Dedicated worker initial module graph, outside settings.** Both
  variants set the document's `script-src 'none'`. Both serve the same
  module root and same-origin static import with **no worker response CSP**.
  `worker-src` is either same-origin or restricted to the exact root URL.
  The root is fetched in both cases; the allowed graph executes and the
  restricted graph fails without requesting its static dependency.
  The stock trace identifies `DedicatedWorkerGlobalScope::FetchAndRunModuleScript`
  on the worker thread for that exact root. The original document does not
  observe a forwarded violation in the controlled window. This is not a
  `Worker()` constructor refusal, a dynamic `import()` in running code,
  or evidence for a forward that never ran.
- **Paint Worklet.** In separate fresh contexts, load the same harmless
  module with owner `script-src 'self'` or `'none'`. The former resolves;
  the latter rejects before the URL reaches interception, with an explicit
  CSP console diagnostic naming the module. The control records worker
  context initialization slices during `addModule()` and checks that the
  original document gets no worklet CSP DOM event in the observation
  window. A diagnostic/rejection is not a forwarded event. **Refuse DOM
  credit for the worklet**, including any unknown worklet type.

Each run proves the owner listener is alive by dispatching a separate
`img-src 'none'` event after the operation's result. Its mark lies inside
the native DOM `EventDispatch`, on the owner's original navigation ID. This
is an observation boundary, **not** a fence across worker threads.

## Pinned source map

Paths below are relative to `third_party/blink/renderer/core/` unless noted.

- `workers/dedicated_worker_global_scope.cc`:
  `DedicatedWorkerGlobalScope::Initialize` binds the network worker
  response CSP to the execution context. `frame/csp/execution_context_csp_delegate.cc`
  queues on the context's networking runner and dispatches inside-settings
  violations on the `WorkerGlobalScope`.
- `workers/dedicated_worker.cc` clones the creator CSP into
  `GlobalScopeCreationParams`. `DedicatedWorkerGlobalScope::FetchAndRunModuleScript`
  passes context and destination `WORKER`/`kWorker` to
  `WorkerOrWorkletGlobalScope::FetchModuleScript`, which builds an
  outside-settings fetcher bound to `OutsideSettingsCSPDelegate`. The
  top-level loader can use browser-supplied `WorkerMainScriptLoadParameters`;
  `loader/modulescript/worker_module_script_fetcher.cc` uses the
  outside-settings fetcher for the dependent request. At this pin,
  `loader/modulescript/module_tree_linker.cc` preserves `context_type_` and
  `destination_` for static dependencies and `frame/csp/content_security_policy.cc`
  maps `WORKER` to `WorkerSrc`. `script-src 'none'` by itself is not the
  blocker tested here.
- `OutsideSettingsCSPDelegate::DispatchViolationEvent` checks
  `DispatchWorkerCSPViolationEventForModuleGraphFetching`, **experimental**
  in `third_party/blink/renderer/platform/runtime_enabled_features.json5`,
  and checks that the producer context is not destroyed. If admitted,
  `ThreadedObjectProxyBase::DispatchCSPViolationEvent` posts cross-thread
  to `ThreadedMessagingProxyBase::DispatchCSPViolationEvent` on the parent
  task runner. That method skips a terminated or missing parent execution
  context, targets the parent's document for a window, or a worker global
  for a nested worker. This source path is conditional; the stock control
  does not enable or empirically prove it.
- `Worklet::addModule` posts the load; `WorkletGlobalScope::FetchAndInvokeScript`
  also uses the outside-settings `FetchModuleScript`, but with `SCRIPT`
  context, which uses the document's script policy. The same experimental
  gate covers forwarding. A main-thread Paint Worklet uses
  `MainThreadWorkletReportingProxy::DispatchCSPViolationEvent`, which
  would queue on the owner's document networking runner. A threaded scope
  uses the cross-thread proxy; the stock control does not distinguish
  an actual forwarding call through either proxy. `ExecutionContextCSPDelegate`
  explicitly skips local DOM dispatch on worklet globals.
- `ThreadedMessagingProxyBase::TerminateGlobalScope` requests termination
  and notifies DevTools. `WorkerThread::PrepareForShutdownOnWorkerThread`
  cancels async work and disposes the scheduler; `PerformShutdownOnWorkerThread`
  later disposes the original global and notifies its proxy. Do not equate
  the early DevTools observation or a replacement's success with those
  native terminal transitions.

## Run

Use the [official Chrome for Testing Linux x64 archive](https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip)
(SHA-256 `3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c`)
and Braid's lockfile-verified Playwright Core `1.64.0`. Set `BRAID_CHROME`
and `BRAID_DRIVER` to their absolute stock paths, and `HOME` and `TMPDIR`
to short private directories. Do not reuse a live profile. The runner
checks exact executable and driver bundle digests.

```sh
node --test "$FIXTURE/worker-oracle-expectations.test.mjs"
unshare --user --map-current-user --net timeout 55 \
  node "$FIXTURE/worker-lifecycle-oracle.mjs"
```

Optional arguments: `classic`, `module`, `worklet`. The runner owns and
closes its browser and contexts. For the paired owner, use the existing
native worker fixture for reservation, exact WorkerToken, actual dispatch
return and signed receipt. The stock cases here specify expectations
and negatives only; they neither add native instrumentation nor change
the collector's refusal policy.
