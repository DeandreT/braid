# Browser security policy

The Server installs `Weft.Api.BrowserSecurityHeaders` before admission, static files
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
Microsoft sign-in is a top-level redirect flow, not a popup or embedded frame;
these headers do not grant access to a provider or loosen authentication checks.
The inert authentication failure document keeps its stricter existing CSP. Its
no-store headers and the Session transport's HTTPS policy remain in force.
The outer helper restores missing headers even when an inner error handler clears
the response. HTTP parsing errors before application middleware remain server-owned.

## Checks

- `BrowserSecurityHeadersTests` checks static GET, HEAD and conditional responses,
  deep links, refusals, cleared responses and the stricter auth policy.
- Published-host checks enumerate every file under `wwwroot` and request it, plus
  the root shell and a nested route, in disabled, Development and Session mode.
- The Angular development server sends the same headers, with HMR and live reload
  disabled. No development-only script or connection allowance is added. A small
  builder wrapper keeps entry assets root-absolute, so nested deep links load.
- The nonshipped HTTPS authentication qualification host retains its separate,
  stricter worker policy. Its `network-frame` and `network-worker` scenarios
  require one trusted, enforced violation for the expected directive and target.
- `tools/browser-fixture/security-headers.test.mjs` checks drift between the
  shipped pipelines, the development server, the session-probe server and the
  HTML/build settings.

Browser suites do not collect CSP violation reports. A collector that can prove
every violation was delivered needs native browser support that current Chromium
and Playwright do not provide; that work is tracked separately. Do not silently
weaken the policy to make an incompatible feature pass.
