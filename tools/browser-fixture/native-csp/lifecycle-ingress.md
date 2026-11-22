# Focus and visibility: original-generation retirement

The existing catalog read binding in `apps/web/src/catalog/catalog-state.ts`
registers window `focus` and document `visibilitychange`. Either can schedule
a timeout and retry an admitted read. `DestroyRef.onDestroy` removes the exact
listeners, cancels its timeout and destroys the read binding. Normal Angular
route deactivation can run that cleanup while the document is still alive.
Closing the browser page or committing a new document is not evidence that
Angular ran it. For example, navigating to the existing Help route can destroy
the catalog route in-place, but the fixture must observe the actual removals
and account for the Help route's work; it cannot assume that the destination has
no producers.

## Chromium paths at `157.0.8090.0`

- Ordinary browser lifecycle update: `WebContentsImpl` →
  `WebViewImpl::SetPageLifecycleState` → `SetPageLifecycleStateInternal` →
  `Page::SetVisibilityState` → `LocalFrame::DidChangeVisibilityState` →
  `Document::DidChangeVisibilityState`.
- Replacement commit: `NavigationRequest::AddOldPageInfoToCommitParamsIfNeeded`
  → old main frame's `WebViewImpl::SetPageLifecycleStateFromNewPageCommit` →
  the same internal visibility route. This variant requires a same-site
  proactive browsing-instance swap that reuses the renderer process; the
  new document is not the old document even on that process.
- Direct unload: `FrameLoader::DispatchUnloadEventAndFillOldDocumentInfoIfNeeded`
  → `Document::DispatchUnloadEvents` on the old document. When still visible,
  this dispatches `visibilitychange` and the legacy alias directly. It does
  not call `Document::DidChangeVisibilityState`.
- Focus: `FocusController::FocusHasChanged` and `SetFocusedFrame` can send
  trusted `focus`/`blur` to a `LocalDOMWindow`. A page's `dispatchEvent(...)`
  is synthetic but can still invoke the same listener and produce real work.
  `isTrusted` is recorded only as DOM provenance, never proof of a widget,
  browser Mojo message, original target, settlement or receiver receipt.

All three visibility paths enter Blink's listener dispatch. The proposal
observes at that common sink and on registration/removal; it does not block a
lifecycle path or return early from DOM dispatch. A registration is a live
producer even when no event is firing. An invocation acquires its own credit
before `once` can remove the registration, and before invoking the listener.
Bulk removal, a reused window or a callback without descendant coverage
refuses. No app change, event suppression, `window.stop`, or injected
`removeEventListener` belongs in a positive packaged test.

## Compose the patch

`lifecycle-ingress.patch` applies **after** `native-phase.patch` and
`document-birth.patch`; it does not modify Inspector, Browser protocol,
Playwright, or the catalog. The source delta is deliberately not part of the
automatic apply script.

1. In the accepted native `WillCreateBraidDocument` path, call
   `LocalDOMWindow::BeginFixtureLifecycleIngress()` before construction;
   a null result refuses. The patch binds the actual token when the document
   is created, before initialization. Retain the state by exact token in the
   native ledger, past disposal and any session/agent replacement. The scope
   does not admit documents with inherited window registrations.
2. At each already-covered native callback/producer handoff, link work to
   `CurrentCallback()` or the exact live descendant via `Publish`. Hold the
   child credit until its real receipt, completion, or independently verified
   cancellation. Call `AcknowledgeCoverage` before the synchronous callback
   exits, **only** if the composing producer ledger actually covered what
   could be published from it. With the patch alone all such callbacks refuse;
   completed timers, promises, or browser forwarding are not receiver proof.
   The existing `probe::UserCallback` scope is inside the lifecycle credit in
   the ordinary listener path. A native handoff there must identify the exact
   invocation, not an unrelated nested callback on the stack.
3. Wait for a real app action to remove its listeners and for descendants
   to settle, without preparing early. Prepare the whole inventory, retain
   this scope's exact token and revision, then close or replace. The hook in
   `LocalDOMWindow::FrameDestroyed` checks after ordinary document/window
   cleanup, including synchronous unload. Require `VerifyRetirement` for that
   original token; no post-close check against its replacement. Native browser
   verification must separately prove the full inventory, disposal, journal
   receipt, and that the context could not gain unrelated producers. A BFCache
   entry, renderer loss, missing original, unresolved handoff, or unknown
   source refuses. Local retirement is not a successful browser cutoff.

## Controls

Build the standalone ledger test from the patched source root:

```sh
clang++ -std=c++20 -Wall -Wextra -Werror -fsanitize=address,undefined -I . \
  "$FIXTURE/lifecycle-ingress-test.cc" -o "$OUTPUT/lifecycle-ingress-test"
timeout 10 "$OUTPUT/lifecycle-ingress-test"
```

`lifecycle-ingress-origin-control.mjs` is an independent branch/provenance
control for checksum-verified **stock** Chrome for Testing `157.0.8090.0` and
lock-verified Playwright Core `1.64.0`. Its launch options differ from the
paired driver; do not substitute it for the patched-pair fixture. Run it with
their paths in `BRAID_CHROME` and `BRAID_DRIVER`, short private `HOME` and
`TMPDIR`, loopback down and an isolated X display:

```sh
unshare --user --map-current-user --net timeout 35 env -u WAYLAND_DISPLAY \
  -u XDG_RUNTIME_DIR -u XDG_SESSION_TYPE -u DBUS_SESSION_BUS_ADDRESS \
  xvfb-run -a --server-args='-screen 0 960x720x24 -nolisten tcp' \
  node "$FIXTURE/lifecycle-ingress-origin-control.mjs"
```

The page's marks show event provenance only and must be corroborated by the
native Chromium path. They are not settlement, durable receipts, or runtime
evidence for the patch. A lifecycle update can leave visibility unchanged;
pass `ordinary-visibility` to require actual trusted visibility delivery from
that path instead of just the ordinary update and trusted focus controls.
In a combined native run, keep the original catalog
GET pending across a legitimate focus or visibility stimulus, verify refusal
and no suppressed response, release that GET, leave via the existing UI, and
require actual listener removal plus descendant receipt. Then challenge
ordinary lifecycle update, replacement commit and direct unload in separate
cases and check original disposal against the frozen proof. Repeat with a
held child, replacement token, reused window, and dropped renderer. Do not
wire the packaged collector until those checks and the browser-wide proof pass.
