# Browser-session components

Implementation status: `Weft.Identity` contains the cookie handlers and
request-scoped, epoch-bound antiforgery composition tracked in
[#112](https://github.com/DeandreT/weft/issues/112). They remain unregistered by
the Server/API. This page describes the implemented contract and required
qualification; it reports no build, discovery, test result or active sign-in.
The underlying [protected session adapter](protected-server-sessions.md) retains
its separate #107 evidence.

## Explicit composition and ownership

A trusted server composition can call `AddWeftBrowserSessions` with one canonical
HTTPS origin, an admitted `DurableSessionKeyProfile`, the restricted session
reader/writer ports and a renewal-scheduling clock. The caller owns the profile,
certificates, ports and pools. The singleton browser owns only its private
standard-antiforgery composition; two distinct transient cookie handlers use
reserved `Weft.Session` and `Weft.Session.Issue` schemes. Registration chooses no
default or forwarding scheme and refuses reserved-name conflicts.

These components add no public route, provider verification, organization or
project admission, payload permission, SQL migration, role or grant. They do not
register authentication with the existing Server/API or change development
identity. The dormant catalog seam in [#114](https://github.com/DeandreT/weft/issues/114),
Entra/public-route and proxy admission in P4d, and Angular sign-in in P4e remain
separate work.

`ObserveAsync` establishes only the canonical session identity.
`AdmitRequestAsync` performs the fresh request checks below. Trusted composition
owns `IssueFreshAsync` and `RevokeLocalAsync`; their primitive inputs must already
be verified server facts. `IssueRequestTokenAsync` requires a positive observation
on a safe, non-transition request. These methods do not expose HTTP endpoints or
turn session identity into business authority. Admission results expose only a
categorical kind through default serialization; handles, epochs, keys, cookies,
tokens and raw provider/database exceptions must stay out of logs and diagnostics.

## Fixed cookies and acknowledged transitions

Exactly two cookie names are used:

| Cookie | SameSite | Fixed properties |
| --- | --- | --- |
| `__Host-Weft.Session` | Lax | Secure, HttpOnly, host-only, Path=/, nonpersistent |
| `__Host-Weft.Antiforgery` | Strict | Secure, HttpOnly, host-only, Path=/, nonpersistent |

Neither has Max-Age or Expires. The shared session carrier protects only the fixed
canonical session handle, using sibling purposes on the admitted profile; it is
not an arbitrary principal/ticket serializer. The raw Cookie header is bounded to
16,384 characters and 128 pairs. Protected-name duplicates, case aliases, chunks,
noncanonical values and disagreement with the parsed selection refuse before
cryptography. Session values are bounded to 2,048 characters; antiforgery cookie
and request-token values to 4,096. Unsupported TLS token-binding purposes refuse.
The configured HTTPS scheme and authority must match exactly; a future host must
first establish trustworthy request/proxy metadata.

The main handler bypasses stock cookie authentication's projected-date expiry
and sliding-refresh behavior. The database controls liveness. It refuses direct
sign-in/sign-out, and challenge/forbid return 401/403 without redirects. The issue
handler reads no old request key, so its once-only sign-in selects a new Store
rather than Renew.

Fresh issue decodes a supplied old carrier even if its row is expired or inactive.
Its retained revocation must be acknowledged before new trusted issuance, handle
allocation or Store. The new session gets an independent handle and login epoch;
fresh issuance preserves the antiforgery cookie. Local revoke likewise awaits
acknowledgement before clearing both cookies and needs no live-reader prerequisite.
An absent session cookie establishes local absence only. Lost acknowledgement,
failed headers or cancellation is not successful issuance/logout: a committed
row can survive failed delivery. There is no retry, compensation or atomic
PostgreSQL-plus-cookie-delivery promise.

A transition is reserved once on private request features. Ordinary observation
and admission in that same request become unavailable; token generation and
repeated trusted helpers refuse. A new request with the new session cookie is
required before issuing its epoch's token.

## Fresh admission, renewal and CSRF

Request state binds the exact context, feature collection and composition, rather
than ambient `HttpContext.User` or copied claims. Original work observes its first
caller/request cancellation; a later waiter can cancel its own wait without
canceling that work. Request cleanup settles owned tasks. No positive identity
cache is shared across requests.

GET, HEAD and OPTIONS are safe methods. Authenticated POST, PUT, PATCH and DELETE
require one exact configured Origin, one canonical `X-XSRF-TOKEN` header and an
unambiguous antiforgery cookie. The private standard antiforgery service validates
them before renewal, body consumption or dispatch; form-body fallback is disabled.
Authenticated unsupported methods, including TRACE and CONNECT, are refused as
CSRF rejection before those effects. This is an admission-policy statement, not
a claim that Kestrel accepts every method/request-target combination.

The standard token's additional data binds the configured organization and exact
login epoch. Same actor and the same antiforgery cookie do not make an old-epoch
request token valid for a new session. Canonical identity comes from the private
session observation, not user-supplied or globally transformed claims. Token
issuance itself is not organization, project or payload authorization.

An authenticated admission makes a separate fresh session observation. The clock
only schedules renewal near the idle deadline; it establishes no liveness or
extension authority. The existing adapter then performs its own fresh read and
one conditional renewal, with at most one separate observation after conflict.
There is no retry, carrier rewrite or new epoch. Original microsecond database
and envelope times and whole-second framework projections remain unchanged.
Unknown/nonlive identity refuses; operational profile/store/header failures stay
unavailable, and caller/request cancellation retains its cause. Homogenized
framework decryption rejection cannot distinguish forgery from loss of a
historical decryption key.

## Qualification scope and limits

The test sources compose two independent owned loopback HTTPS/Kestrel instances
with real restricted PostgreSQL, separate pools and fresh profiles. Their intended
oracles include revoke acknowledgement before fresh Store, no issue Renew,
same-actor epoch separation, before-body refusal, final-fraction database expiry,
renew/revoke races, real lost COMMIT acknowledgement, cancellation/header failures,
and normal API/database restart and certificate/key rotation or loss. Rotated
certificates remain fixture-owned until all borrowing hosts/profiles settle;
unproven settlement retains dependencies and fails cleanup.

A separate standard-antiforgery reference checks that the same cookie and actor
pass the framework's crypto/ClaimUid stages and expose the old additional data;
its deliberate acceptance is only a reference oracle for the product's epoch
refusal. It does not bypass the product path. Source presence does not establish
any of these runtime results; qualification remains linked to #112.

Actual HTTPS/header checks do not establish a browser's SameSite or `__Host-`
enforcement. In-process host/profile restart is not an independent operating
system restart. Warm keys do not prove durable recovery, instant distributed
revocation or arbitrary historical certificate provenance. Hosted authentication,
provider callback verification, proxy trust, public login/logout/status routes,
business authorization and Angular activation remain outside this increment.
