# Dormant browser authentication HTTP protocol

The opt-in Identity/API composition connects the fixed Entra adapter to
the existing server sessions. It adds no shipped Server activation, Angular
sign-in, provider logout, identity provisioning or SQL/grants.

Register `AddWeftBrowserSessions`, the fixed Entra client, and
`AddWeftEntraBrowserAuthentication` on the same service collection with the same
borrowed admitted profile. Internal reference-identity checks prove this shared
ownership as well as organization/origin equality. The HTTP registration takes an
explicit Disabled, Development or Session profile; call `MapWeftAuthentication`
only in the intended composition. Non-session status resolves no sensitive
session/provider/profile services. Development requires its existing validated
loopback HTTP catalog and keeps the old context/token JSON. Session requires the
canonical HTTPS origin. The later host increment owns actual mode selection,
listeners/proxies, credentials, durable profile delivery and privacy settings.

## Supported contract and runtime activation

`AddWeftApi` publishes the complete authentication schemas and all five supported
route descriptions, including the closed Development/session catalog-context
union when catalog routes are described. OpenAPI is a static supported-contract
superset, not a live endpoint or enabled-mode inventory, consistent with the
disabled catalog contract. Repeated generation and Disabled/Development HTTP
registration order do not select or narrow this document.

`AddWeftAuthenticationHttp` and `MapWeftAuthentication` remain separate dormant
opt-ins. Publishing the contract installs no authentication services, defaults,
cookies or redirects and maps no actual auth route. Explicit Disabled/Development
mapping exposes only status; Session mapping exposes the full runtime protocol.
Generated clients describe supported operations, not proof of availability or
authority. Generate OpenAPI and TypeScript from the standard API export; do not
activate authentication just to export its contract.

All protocol responses are no-store/no-ETag/no-referrer. Inputs have no body,
account selector or return target. The fixed session-mode routes are:

| Route | Required fixed header | Result |
| --- | --- | --- |
| GET `/api/auth/session` | `X-Weft-Session: 1` | Explicit mode; session arm has closed identity state and memory-only tokens/signal |
| POST `/api/auth/login` | `X-Weft-Login: 1` | Bounded approved authorization URL; caller navigates top-level |
| GET `/api/auth/callback/entra` | Complete actual OIDC protocol | Acknowledged new session then303 `/`, or readable fixed failure |
| POST `/api/auth/logout` | `X-Weft-Logout: 1` |204 only after local revoke acknowledgement/absence and cookie deletion |
| POST `/api/auth/forget-local` | `X-Weft-Forget-Local: 1` | Explicit revocation-unconfirmed local forgetting, restricted below |

Unsafe actions require exact Origin and the separate `X-Weft-Auth-Token`.
Authenticated login/logout additionally require the existing epoch-bound
`X-XSRF-TOKEN`. The standard anonymous antiforgery composition has a separate
`__Host-Weft.AuthAction` Secure/HttpOnly/Strict cookie and fixed protection purpose.
Only that service sees a private anonymous HttpContext facade with fresh empty
Features and actual Request/Response delegation. It never mutates actual User or
shares the browser session's per-context antiforgery cache. Invalid or
foreign-audience submitted tokens are request rejections; operational protection
failures remain sticky across framework fallback. No custom token crypto is
introduced.

Callback query names are exactly code, state, session_state, client_info, iss,
error, error_description and error_uri. The strict bounded success/error arms
reject unknown, token-bearing, repeated/case-aliased or mixed code/error input.
Optional client_info is bounded512 and has no authority. Only final request-bound
verified facts trigger a fresh exact enabled binding/current-principal read and
fresh session issuance. Old-session revoke acknowledgement precedes any new Store,
including same-actor login. Provider/email/role claims never supply business
permissions; catalog operations retain fresh organization/project/capability ACLs.

Malformed-carrier forgetting is a separate explicit action, not failed-logout
fallback. Positive separate AF precedes the actual current carrier classifier.
Only its exact pre-writer Denied permits a once-only internal transition to clear
local cookies. Absent/decoded carriers return not-applicable409. Prior failed
transitions, operational/integrity failures, cancellation or lost acknowledgement
never become permission to forget. Successful forgetting reports
`revocationUnconfirmed`: a server row may still exist. No Remove/Store occurs.

Positive session status and catalog context share `identityContext`: `v1.` plus
the canonical base64url SHA256 digest of the fixed domain/org/principal/private
epoch encoding. The catalog retains its exact admitted identity's presentation
signal through existing admission/token mapping; it does not reconstruct epoch
from claims or status. Development omits this field. The signal is memory-only UI
invalidation data, never a credential, role, write cancellation proof or authority
lease. Never send it back for authorization or persist it/tokens in storage,
URLs, traces or logs. Cross-request equality is not an atomic identity guarantee.

Callback401 reads `Sign-in could not be completed`;503 reads `Sign-in is
temporarily unavailable`. The static document contains only a fixed Return to
return link to `/`, no dynamic/provider/request values. Navigation/tab closure is
not successful sign-in; a future UI must explicitly observe fresh status and
catalog admission. Operational failure is not anonymous success or automatic
retry. Lost database acknowledgement/header delivery remains ambiguous without
retry or compensation.

The orchestrator closes method admission, cancels its owned lifetime, and drains
all actual owned work before private antiforgery disposal. It joins the browser's stored
handler/observation/admission owners even if a public wait already canceled,
without clearing their failure or transition latches. It never disposes borrowed
browser/client/profile/pools/certificates. Host shutdown must await this boundary
before borrowed dependencies. Hosts must also await every public browser call they
start before ending its request scope, including cancellation and failure paths.
The stored core drain does not own public caller wrappers that can still access
the request context after a stored task completes. No instant distributed
revocation, browser cookie enforcement, live Entra or serverless behavior is
established by source presence.
