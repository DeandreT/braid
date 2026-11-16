# Browser security policy

The Server installs `Braid.Api.BrowserSecurityHeaders` before admission, static files
and routing in both its Development/disabled and Session pipelines. Packaged images
run those same pipelines. The policy is an HTTP response header, not a meta tag:

```text
default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
```

The default denies everything unless a directive admits it. Scripts, styles, fonts,
API connections and workers use only the application's origin. Images additionally
allow `data:` for embedded images. Inline styles remain necessary for Angular's
runtime component style elements and dynamic layout sizes and CSS variables in the
workbench. The allowance is confined to styles: inline scripts, event handlers,
`eval` and WebAssembly compilation are not allowed. The page never receives
`unsafe-eval` or `wasm-unsafe-eval`, including when a feature uses a worker. Worker
implementation and worker response policy remain separate boundaries.

The shipped editor assets have a separate response policy confined to the exact
public origin's `/edit/` path. It admits `wasm-unsafe-eval` only for that worker,
denies network connections and nested workers, and retains the common security
headers. Missing editor paths return empty 404 responses under the shell policy.
The Angular development target excludes these assets from its ordinary asset
handler and serves the same six-file package through the scoped handler; it does
not grant Wasm permission to the page or other development assets.

The shell has no `<base>` element because `base-uri 'none'` forbids it. The router
gets `/` through `APP_BASE_HREF`; the build emits root-relative asset URLs so direct
navigation to a nested route still loads the same scripts and styles. Production
critical-CSS inlining is disabled because its deferred stylesheet loader uses an
inline `onload` handler. Angular's component styles retain the style-only allowance.
The separate session test entry uses its own explicit asset prefix and router base.

All responses also carry `X-Content-Type-Options: nosniff`,
`Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Resource-Policy: same-origin`. These prevent MIME sniffing, suppress
referrer disclosure and separate cross-origin windows and resource embedding.
Microsoft sign-in is a top-level redirect flow, not a popup or embedded frame.
The inert authentication failure document keeps its stricter existing CSP. Its
no-store headers and the Session transport's HTTPS policy remain in force.
The outer helper restores missing headers even when an inner error handler clears
the response. HTTP parsing errors before application middleware remain server-owned.

The mapping editor's dedicated worker has a separate policy on the shipped
`/edit/worker.js`, `/edit/core.js` and `/edit/core.wasm` assets. It allows wasm
compilation only inside that worker and confines script imports to the same
origin's `/edit/` directory. The shell never receives `wasm-unsafe-eval`.
The development builder serves those files through its own middleware rather
than Angular's general static middleware. Missing editor assets return 404
instead of the HTML shell.

## Checks

- `BrowserSecurityHeadersTests` checks static GET, HEAD and conditional responses,
  deep links, refusals, cleared responses and the stricter auth policy.
- Published-host checks enumerate every file under `wwwroot` and request it, plus
  the root shell and a nested route, in disabled, Development and Session mode.
- The Angular development server sends the same headers, with HMR and live reload
  disabled. The editor asset exception has the same path boundary as the
  packaged host. A small builder wrapper keeps entry assets root-absolute, so
  nested deep links load without a base element.
- The nonshipped HTTPS authentication qualification host retains its separate,
  stricter worker policy. Its `network-frame` and `network-worker` scenarios
  require one trusted, enforced violation for the expected directive and target.
- `tools/browser-fixture/security-headers.test.mjs` checks drift between the
  shipped pipelines, the development server, the session-probe server and the
  HTML/build settings.
- `tools/browser-fixture/editor-dev-server.test.mjs` checks the development
  worker policy, exact file allowlist, host/origin restrictions and refusals.

Browser suites do not collect CSP violation reports: current Chromium and
Playwright cannot confirm delivery of every violation.
