# Browser-to-renderer CSP delivery oracle

This offline control uses a top-level document with an already committed
cross-site iframe. It traces the browser and both renderers on Chromium
`157.0.8090.0` (source revision `2ea1b71af70d7fc5d41325dda8df37c1f98ac531`).
A normal click in the child changes its location. All responses are
intercepted fixture data from reserved `.invalid` hosts; loopback is down,
the sandbox is enabled, and no browser feature override is required.

| Case        | Native source                                                                                                                                               | Required result in the original parent                                                                                                                                                                   |
| ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `blink`     | Set a same-origin `img` URL under `img-src 'none'`. No image request reaches interception.                                                                  | One trusted `img-src` DOM event, no CSP Mojo receive.                                                                                                                                                    |
| `redirect`  | Child follows an allowed same-site URL; its synthetic `302` redirects to a site forbidden by the parent's `frame-src`. The final URL must not be requested. | Browser `OnRequestRedirected`, exact `LocalFrame` CSP Mojo receive, then one trusted `frame-src` DOM event on the original document. `ERR_BLOCKED_BY_CSP` and the matching audit are checked separately. |
| `ancestors` | Parent allows the child destination, but the final response sets `frame-ancestors 'none'`.                                                                  | `ERR_BLOCKED_BY_RESPONSE`, exact CSP Mojo receive, and a `frame-ancestors` audit naming the original parent. **No** `frame-ancestors` DOM event. The subsequent Blink image event still dispatches.      |

The `form` control exercises a different receiver. The original out-of-process
child submits a top-level navigation to an allowed action URL. Its synthetic
`302` redirects to a site forbidden by the child's `form-action`. The browser
aborts the top navigation with `ERR_ABORTED` and sends the CSP notification to
the original **child**, not the destination top document. Both original
loaders stay unchanged. The reported URL is the initial action URL; the
blocked redirect is never requested.

The test asserts distinct renderer PIDs for the original receiver and its
live peer; a browser PID from the browser session; the unchanged receiver frame,
loader and trace navigation ID; the exact native Mojo interface and method;
and a trusted receiver listener mark **inside** a native CSP `EventDispatch`
slice. The audit is a receipt barrier for the test, not a DOM event:
Blink emits it synchronously after posting any applicable CSP event on the
original networking task runner. After the audit, the test queues the image
event on that same runner and waits for it before checking that the
`frame-ancestors` event is absent. No quiet-period sleep is used. Markers and
the audit do not attest arbitrary web pages, receiver settlement or durable
receipt.

## Pinned source map

- `content/browser/renderer_host/navigation_request.cc`:
  `OnRequestRedirected` → `CheckContentSecurityPolicy` → `CheckCSPDirectives`.
  Parent `frame-src` uses `RenderFrameHostCSPContext(parent)`. Form submission
  uses the separate initiator context from `GetInitiatorDocumentRenderFrameHost`
  (resolves the original document token and process; may return null). The
  `form` control proves delivery to the original initiator while the separate
  destination document remains alive; initiator loss is not covered.
- `content/browser/renderer_host/render_frame_host_csp_context.cc`:
  `ReportContentSecurityPolicyViolation` forwards on the associated
  `blink.mojom.LocalFrame` remote. Cross-origin `frame-src` blocked URLs
  can be sanitized to origin; do not require the full target path.
- `content/browser/renderer_host/ancestor_throttle.cc`:
  `EvaluateFrameAncestors` applies the final response policy before commit and
  uses the disallowed ancestor's `RenderFrameHostCSPContext` to report.
- `third_party/blink/renderer/core/frame/local_frame_mojo_handler.cc`:
  `ReportContentSecurityPolicyViolation` forwards into Blink CSP and passes
  `context_frame` only for `FrameAncestors`. In
  `core/frame/csp/content_security_policy.cc`, `ReportViolation` posts the report
  and emits the audit issue, but calls `DispatchViolationEvent` only with a
  delegate **and without** `context_frame`. Otherwise there is no DOM
  obligation for this path.
- `core/frame/csp/execution_context_csp_delegate.cc`: admitted violation
  dispatch is posted to `TaskType::kNetworking`; a connected element can be the
  receiver, otherwise the document is. The no-element Mojo path here must
  target the original document.
- `mojo/public/cpp/bindings/lib/interface_endpoint_client.cc`:
  `HandleValidatedMessage` traces the receiver and generated IPC hash. The
  generator `mojo/public/tools/bindings/generators/mojom_cpp_generator.py`
  takes the first eight SHA-256 hex digits of
  `blink::mojom::LocalFrame::ReportContentSecurityPolicyViolation` (`4593d00c`).
  Stock Chrome can export `Receive mojo message` rather than the symbolic
  name; require the exact interface and hash, not a generic receive.

## Run

Use the [verified official Chrome for Testing Linux x64 archive](https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip)
(SHA-256 `3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c`)
and lock-verified Playwright Core `1.64.0`. The runner verifies the Chrome
executable and driver bundle. Set `BRAID_CHROME` and `BRAID_DRIVER` to their
absolute paths and `HOME` and `TMPDIR` to short, private directories. Each
case owns its browser/context and tears it down. Use a private X display
and no credentials or shared profile:

```sh
node --test "$FIXTURE/browser-origin-csp-trace.test.mjs"
for mode in blink redirect ancestors form; do
  unshare --user --map-current-user --net timeout 40 env \
    -u WAYLAND_DISPLAY -u XDG_RUNTIME_DIR -u XDG_SESSION_TYPE \
    -u DBUS_SESSION_BUS_ADDRESS xvfb-run -a \
    --server-args='-screen 0 960x720x24 -nolisten tcp' \
    node "$FIXTURE/browser-origin-csp-control.mjs" "$mode" --stock-oracle || exit
done
```

## Native owner's paired acceptance

1. Replace `--stock-oracle` with `--paired`, using the owner's
   already-admitted `BRAID_CHROME_SHA256` and `BRAID_BUNDLE_SHA256` and private
   pipe. No new native patch is required to check the behavior. Missing
   trace metadata or unmatched evidence is a failed control, not a fallback.
2. For any separate native receipt claim, reserve the exact parent generation
   before navigation to `http://braid.invalid/fixture` using the owner's
   existing enrollment. Use the exact parent token captured before the click.
   The existing hard-coded `worker-src` receipt predicate cannot establish
   this `frame-src` case. If the paired fixture already exposes per-source
   credits, require browser publication, exact original receiver, dispatch
   return, and the matching signed acknowledgment separately. Do not infer
   any of these from tracing or from an inspector audit.
3. Keep the `frame-ancestors` case as a negative DOM control: verify the
   report/audit path instead of demanding a dispatch or minting a DOM
   receipt. If testing replacement or detach, require an original-scoped
   result or refusal, never delivery credited to the replacement. The
   controls above do not qualify those races or a browser cutoff.
