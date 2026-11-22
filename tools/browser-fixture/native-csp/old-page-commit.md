# Old-page visibility during a replacement commit

This test uses three intercepted HTML responses in a fresh context. It does
not run Angular or change Chromium's production code. Requests outside the
three fixture URLs are blocked. The browser sandbox stays enabled, and the
CDP connection is the launch-owned pipe.

## What establishes the path

On Chromium `157.0.8090.0`, `NavigationRequest::AddOldPageInfoToCommitParamsIfNeeded`
includes the old frame token only if
`RenderFrameHostImpl::ShouldDispatchPagehideAndVisibilitychangeDuringCommit`
finds a same-site navigation between **unrelated** site instances using the
**same** renderer process. `RenderFrameImpl` resolves the old frame token and
calls `WebViewImpl::SetPageLifecycleStateFromNewPageCommit` on that old frame.
This differs from both `WebViewImpl::SetPageLifecycleState` (an ordinary
lifecycle update) and `Document::DispatchUnloadEvents` (direct teardown).

The control starts tracing before the stimulus. An original-document marker,
a deliberately synthetic event, the original document's trusted hidden
event, and the replacement marker must be distinct and ordered. The trusted
listener must run **inside** the native `EventDispatch` visibility slice,
which must in turn be inside the expected lifecycle slice on the original
renderer PID and thread. A commit requires the replacement to use the same
renderer, too. A commit trace elsewhere or a same-renderer reload alone is
not evidence. Page marks are only identifiers for this known test code; they
are not receipts or an authority for arbitrary pages.

## Run the stock controls

Use the [official Chrome for Testing `157.0.8090.0` Linux x64 archive](https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip)
(SHA-256 `3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c`),
and Playwright Core `1.64.0` verified against Braid's lockfile. Put the
absolute executable and package directory in `BRAID_CHROME` and
`BRAID_DRIVER`. The script checks their Chrome executable and driver
bundle digests against the stock pins.
Set short, private `HOME` and `TMPDIR` directories and run each case
on a fresh, isolated X display in the loopback-down fixture namespace:

```sh
node --test "$FIXTURE/old-page-commit-trace.test.mjs"
for mode in stock-default stock-proactive stock-coop; do
  unshare --user --map-current-user --net timeout 50 env \
    -u WAYLAND_DISPLAY -u XDG_RUNTIME_DIR -u XDG_SESSION_TYPE \
    -u DBUS_SESSION_BUS_ADDRESS xvfb-run -a \
    --server-args='-screen 0 960x720x24 -nolisten tcp' \
    node "$FIXTURE/old-page-commit-control.mjs" "$mode" || exit
done
```

- `stock-default` is the negative control: same origin, distinct paths,
  stock Playwright arguments including `--disable-back-forward-cache`.
  The original delivers from direct unload even if Chromium reuses its
  renderer.
- `stock-proactive` omits only Playwright's cache-disable argument in this
  isolated control. The otherwise eligible new history entry exercises
  Chromium's proactive same-site browsing-instance swap and old-page commit.
- `stock-coop` keeps every launch argument at its default. Only the
  replacement HTML sets `Cross-Origin-Opener-Policy: same-origin`.
  Chromium's normal COOP group swap for this potentially trustworthy
  loopback origin can reuse the renderer in the one-tab context. The trace,
  not the header, is the assertion. This is a **COOP** swap, not a claim that
  the proactive BFCache branch ran.

## Handoff for the paired build

Use the native owner's existing `catalog-receiver-lifecycle.patch` and
fixture instrumentation. No new native hooks, build, launch flags or app
changes are needed. The paired launch guard rejects options such as
`ignoreDefaultArgs`, so use `paired-commit` (the same intercepted response
header as `stock-coop`) and `paired-unload` (no COOP). Both use the original
URL already recognized by the catalog fixture reservation and do not start
the packaged server, services, or suite.

Use the same namespace and X display wrapper with `DEBUG=pw:browser`, the
admitted `BRAID_CHROME` and `BRAID_DRIVER` pair, and the owner's pinned
`BRAID_CHROME_SHA256` and `BRAID_BUNDLE_SHA256`. Capture stdout and stderr
together to a fresh, task-owned file and require a successful runner exit.
Then check the file with:

```sh
node "$FIXTURE/old-page-commit-control.mjs" verify-native "$LOG" commit
# For paired-unload, pass "unload" instead of "commit".
```

The paired runner reserves the document before navigation, reads the exact
original generation before replacement, and checks that the original
listener is enrolled with no prior trusted visibility delivery. The
post-run check requires exactly one `BRAID_FIXTURE_VIS` for that original
token: source `2` for commit or `3` for unload. It rejects source `1`
(ordinary lifecycle update), the wrong token, duplicate hits, and mixed
sources. It needs no post-commit read on a detached page session.

This establishes one original callback's actual entry path. It does not
establish receiver settlement, document disposal, or a browser-wide CSP
cutoff.
