# Qualification browser transport admission

The nonshipped HTTPS probe uses a parent-owned literal-loopback CONNECT proxy, one
browser-root Fetch owner, and an earliest probe request/response gate. This does not
change shipping authentication or establish identity. Cookies, antiforgery, OIDC
validation and session state remain the real product implementations.

The bounded claim covers HTTP/HTTPS/WS/WSS destinations and product delivery in the
trusted controlled scenario. It is not a packet sandbox and makes no WebRTC, raw
UDP, DNS, QUIC or OS external-handler guarantee.

The proxy permits CONNECT only to its configured literal IPv4 application port and
copies opaque TLS; a peer reset ends a tunnel without counting as a proxy failure.
Besides the proxy vector's single forbidden destination, which Chromium may also
preconnect speculatively, it must refuse nothing. The browser uses a single explicit
proxy and subtracts implicit loopback bypass. Only the provider host and Chromium's
own account and search hosts bypass it, and the browser resolver maps each of them
to NOTFOUND, so speculative and background connections fail inside the browser.
Browser-to-application TLS and private NSS trust remain end-to-end.

The root Fetch owner replaces Playwright routes. It privately registers a fresh
single-use transport proof bound to the raw target, method, process generation and
request predicates, awaits acknowledgement, then adds only that proof plus any named
negative antiforgery mutation. It never copies Cookie or substitutes URL, method or
body. The probe atomically consumes and removes the proof before product middleware.
Interception misses, preflights, unrecognized workers, WebSocket upgrades and stale
proofs must fail at the default-deny host/proxy boundary. A CORS preflight that
Chromium exposes to Fetch is continued unaltered without a proof, so the host's
refusal is its witness; worklet module fetches arrive without a page network id.
Chromium's automatic `/favicon.ico` probe is failed by the Fetch owner before any
grant. The owner reads an admitted login response body at the response stage, before
delivering it unmodified, to obtain the authorization URL the provider navigation
must match. When the page cancels a product request whose pause is held, the
continuation may settle as canceled only after the page's own network events report
that exact request canceled. Its unused proof is then withdrawn; withdrawal removes
only that pending registration and never grants. Any other lost pause still fails.

An outermost OnStarting callback checks final inner response headers before write.
Only the real callback's 303 to `/` may redirect. Refresh, other Location, upgrades
and proof echo abort. Chromium may resend an aborted request on a proven connection;
the resend carries the consumed proof and is refused. Real Set-Cookie remains
unmodified. Responses without their own cache policy are marked no-store, so reloads
re-enter admission instead of sending unadmitted cache validators.
Qualification-only CSP denies workers, frames and objects; worklet SCRIPT-context
ambiguity needs distinct destination/provenance proof rather than relying on
worker-src.

Shutdown revokes admission while denial boundaries stay installed, joins original
context/browser closure and protocol tasks, then drains proxy, IPC, probe/product
callbacks and dependencies. Unconfirmed settlement retains owners and private
database/ring/NSS files. Diagnostics contain fixed categories/counts/booleans only.
