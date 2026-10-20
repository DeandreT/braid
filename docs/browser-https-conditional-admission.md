# Conditional browser qualification admission

The nonshipped browser qualification uses a fixed literal-loopback CONNECT proxy, one
browser-root Fetch owner and an earliest probe gate. A transport proof is conditional:
it is valid only when closed registration AND actual final-wire target/method/generation/
authority/metadata predicates succeed. It never establishes identity or overrides normal
cookies, antiforgery, OIDC checks or session authority.

Fetch derives finite expected metadata from owned operations and private phases rather
than requiring early generated Sec-Fetch fields. CORS-mode module scripts and fonts
carry the page Origin even same-origin; style and image loads carry none. Because
the Fetch owner fulfills the provider redirect, Chromium restarts the callback from
the page origin, so the callback, its return and a replay carry same-origin site
metadata. Browser-initiated loads carry none, and a reload keeps the site of the entry
it reloads. Continuations omit Cookie and every
sec-fetch- field; Chrome computes the actual values and the host atomically consumes,
checks and strips the one-use proof before product. No metadata synthesis or permissive
retry is used. Wrong normal-flow expectations fail closed.

Audio/Paint module graphs preserve distinct destinations, JSON/text remain refused,
non-window CSS modules and worklet dynamic imports reject. Qualification launch disables
AnimationWorklet,CSSLayoutAPI,SharedStorageAPI,AdInterestGroupAPI,Fledge,Parakeet using
the supported disable-blink-features switch. Audio/Paint stay enabled; ordinary worker/
frame CSP and service-worker blocking remain. Exact binary/flag behavior requires actual
qualification; no constructor patch or global profile/trust change is used.

Actual response headers pass the outermost OnStarting gate before write. Only the real
callback303 '/' redirects normally. Named private transport adversaries may mutate one
harmless actual response or omit/alter one transport proof to exercise fail-closed
boundaries. They never change positive authentication/provider/cookie responses and
cannot forward a provider URL. Their exercise and refusal oracles are finite, explicit
and joined to original operations, not inferred from absent requests.

The bounded claim covers HTTP/HTTPS/WS/WSS destination and product delivery in the
trusted controlled browser/CDP scenario, not a compromised renderer/other privileged
client or a WebRTC/UDP/DNS/QUIC/external-handler sandbox. No raw private request/error/
proof/cookie material enters reports or artifacts. Cleanup joins original context,
browser, protocol, proxy, private IPC and probe/product callbacks before dependencies;
unconfirmed settlement retains owners and private files.
