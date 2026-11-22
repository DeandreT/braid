# Original DOM target for Blink-local CSP violations

This is a **stock Chromium 157.0.8090.0 behavior oracle**, not a browser-wide
collector, a native receipt, or a substitute for the packaged pair. The fixture
intercepts only its `.invalid` documents and blocks other URLs. The browser
sandbox stays enabled, service workers are blocked, and the namespace has
no reachable interfaces. No CSP policy is overridden.

## Controls

- The fixture navigates to an actual `text/html`, `application/xhtml+xml`,
  or `image/svg+xml` response, then holds that `Document` object. MIME, DOM
  classes and root names/namespaces distinguish the HTML document from XHTML
  and SVG XML documents. A namespace-created inline `<style>` is blocked by
  that document's `style-src 'none'` response header. With the element still
  connected to its original document, its listener sees itself at target; the
  original document's capture listener sees the same event object.
- For each document type, remove the style in the same task that inserts it.
  The style was connected when checked but is disconnected when the event
  dispatches. The original document is then the actual DOM target, not the
  detached element.
- In the HTML adoption case, move the same style to a second, already loaded
  same-origin document in an iframe before the task returns. At dispatch it is
  still connected, but its owner is different. Only the original document is
  the target. The receiver document runs its own policy; the original CSP
  event is not a notification about that document.
- In the open and closed author shadow cases, listeners on the exact style
  element and shadow root see the inner style as `event.target`. Listeners on
  the host and original document see the host as the retargeted `event.target`
  of that **same event object**. An outside listener's open-root path still
  starts at the style; its closed-root path starts at the host. The outside
  host's at-target phase is not evidence that the host was the original
  receiver. All path checks run during the listener, not after dispatch.

The runner requires actual listener invocation and checks event identity,
trust, target, current target, phase, the original document URL, and the
`style-src-elem` fields. It records no callbacks before returning from the
insertion/mutation task. No fixed delay is used to infer delivery: the
original capture listener is a positive barrier and the snapshot is read
on a later JS turn. One intercepted case does not prove absence of other
delayed events.

## Native source boundary

The pinned Chromium revision is `2ea1b71af70d7fc5d41325dda8df37c1f98ac531`.
Under `third_party/blink/renderer/core/`:

1. `dom/document_init.cc`: MIME selection followed by
   `DocumentInit::CreateDocument` creates `HTMLDocument`,
   `XMLDocument::CreateXHTML`, or `XMLDocument::CreateSVG`. Do not generalize
   an HTML-only reservation to either XML mode.
2. `css/style_element.cc`: `StyleElement::CreateSheetOrModule` runs the
   inline CSP check with the connected element. Both HTML and SVG style tags
   use this path. `frame/csp/csp_directive_list.cc` reports the inline
   violation with that element.
3. `frame/csp/execution_context_csp_delegate.cc`:
   `DispatchViolationEvent` posts to the original execution context's
   `TaskType::kNetworking` runner with the violation data and element.
   `DispatchViolationEventInternal` resolves the live document via the
   delegate's `LocalDOMWindow`. It calls `element->DispatchEvent` only
   if `element->isConnected()` **and**
   `element->GetDocument() == document`; otherwise it calls
   `document->DispatchEvent`. The test keeps the policy document alive. It
   does not test navigating it away, reusing the window, or tearing it down.
4. `events/security_policy_violation_event.cc` constructs the event as
   bubbling, composed and noncancelable. `dom/events/event_dispatcher.cc`
   starts from the chosen node. `EventPath::CalculateAdjustedTargets`,
   `NodeEventContext::HandleLocalEvents` and `Event::composedPath` give
   each listener its tree-scope view. A renderer check must preserve the
   original native receiver and event identity, not derive it from a
   serialized `target` or `composedPath()[0]` seen outside the root.

The existing `ContentSecurityPolicyTest.FixtureLocalCaptureIsNotBrowserCutoff`
in `native-phase.patch` remains the exact earlier native phase/return control
for the paired build. It uses a document-targeted `worker-src` violation and
does not cover these `style-src-elem` cases. No native patch or packaged
suite wiring is part of this oracle; no inference is made about raw pointer
identity, native credits, element adoption across an origin boundary,
non-HTML flavors other than XHTML/SVG, or signed receipts.

## Run

Use the [official Chrome for Testing Linux x64 archive](https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip)
(SHA-256 `3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c`)
and Braid's lockfile-verified Playwright Core `1.64.0`. Set `BRAID_CHROME` and
`BRAID_DRIVER` to the absolute stock executable and package directory.
The runner verifies both exact digests. Set `HOME` and `TMPDIR` to short,
private directories; use no shared profile.

```sh
node --test "$FIXTURE/dom-target-expectations.test.mjs"
unshare --user --map-current-user --net timeout 65 \
  node "$FIXTURE/dom-target-control.mjs"
```

Optional positional case names: `html`, `xhtml`, `svg`, `removed`,
`xhtml-removed`, `svg-removed`, `adopted`, `shadow-open`, `shadow-closed`.
Each case gets a fresh context, and the runner closes its own browser.
