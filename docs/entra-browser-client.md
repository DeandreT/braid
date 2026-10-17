# Dormant Entra browser client

The P4d1 adapter in [#116](https://github.com/DeandreT/weft/issues/116) completes
the standard OpenID Connect protocol for one configured Microsoft organizational
tenant and client. Protocol and deployment qualification are separate gates. The local HTTPS
fixture does not establish a configured live tenant or a deployment.

The Server does not register this adapter or expose login/callback routes. Existing
disabled and Development behavior is preserved. P4d2 owns dormant HTTP and binding
orchestration; P4d3 owns explicit host activation; P4e owns Angular sign-in.

## Trusted composition and public API

`EntraClientOptions(Guid tenantId, Guid clientId, string publicOrigin)` admits one
nonzero tenant/client GUID and a canonical HTTPS root origin. It rejects the
reserved Microsoft consumer tenant. Configuration is server-owned: requests
cannot choose provider, tenant, application, endpoint, scopes or return URL.

The trusted composition root supplies an already admitted durable session-key
profile and an explicit RSA client certificate:

```csharp
services.AddWeftEntraClient(
    new EntraClientOptions(configuredTenant, configuredClient, configuredOrigin),
    admittedSessionProfile,
    entraClientCertificate);
```

This registration adds a lazy `EntraBrowserClient`, the reserved private
`Weft.Entra` handler and a refusing sign-in sink. It adds no authentication default
or application cookie and builds no service provider. There is no public generic
provider, event, token-validator, transport or protect/unprotect override.

The facade has two request-bound operations, both requiring a cancellation token:

| Operation | Result and responsibility |
| --- | --- |
| `BeginLoginAsync(HttpContext, CancellationToken)` | Runs the fixed standard challenge and returns `EntraLoginChallenge.AuthorizationUrl`. A later HTTP layer deliberately maps that bounded server-generated URL to its navigation DTO. |
| `VerifyCallbackAsync(HttpContext, CancellationToken)` | Runs the actual standard callback and returns internally constructed `EntraVerifiedLogin` with fixed organization, exact `ExternalIdentityKey` and verified ID-token expiry. |

Results are sealed immutable objects with internal constructors, private
current-request provenance, redacted `ToString` and `JsonIgnore` accessors. Default
JSON serialization exposes no subject, state, nonce, token or authorization URL.
There is no public API accepting caller-provided facts as verified evidence.
Reuse or overlap in the same request is refused. Direct middleware callback work
requires the facade's private operation marker; the later HTTP layer must route
and gate the callback explicitly.

Admission expects bodyless `POST /api/auth/login` or `GET /api/auth/callback/entra`,
HTTPS matching the configured origin and no path prefix. These are facade guard
preconditions; this module registers neither route. The verified deadline is the
ID token's whole-second UTC expiry. Session issuance retains its separate existing
framework-second projection and database-microsecond bounds.

`TokenValidated` only records a private candidate. Final `TicketReceived`, after
nonce and token-response checks, handles the response before publishing verified
evidence. Stock sign-in and redirects cannot create a second application session.
The adapter performs no binding lookup, provisioning, role grant or session write.

## Fixed organizational Microsoft policy

The package profile pins ASP.NET Core OpenID Connect **10.0.12** and
Microsoft.IdentityModel.Protocols.OpenIdConnect **8.19.2**, with the IdentityModel
8.19.2 dependency closure retained in the NuGet lockfiles.
Microsoft.Identity.Web, MSAL and downstream token acquisition/cache
are not part of this increment.

For configured tenant `T`, all logical destinations are fixed:

| Purpose | Permitted value |
| --- | --- |
| Issuer | `https://login.microsoftonline.com/T/v2.0` |
| Discovery | `https://login.microsoftonline.com/T/v2.0/.well-known/openid-configuration` |
| Authorization | `https://login.microsoftonline.com/T/oauth2/v2.0/authorize` |
| Token redemption | `https://login.microsoftonline.com/T/oauth2/v2.0/token` |
| Signing keys | `https://login.microsoftonline.com/T/discovery/v2.0/keys` or the exact `/common/discovery/v2.0/keys` equivalent |

There are no consumer/common/organizations authorities, sovereign/regional hosts,
External ID/B2C, endpoint-query aliases, custom-signing-key fallback or redirects.
Pre-send method and exact-address admission runs even on reused connections.
Discovery fields are checked before the standard retriever follows its JWKS URL.

Common signing-key documents can contain multiple issuer scopes. The private
retriever keeps only exact configured-issuer keys or the recognized tenant template
instantiated with configured `T`; missing, ambiguous and unrelated scopes are
excluded. It rebuilds eligible signing material through standard IdentityModel
conversion before initial, refresh or last-known-good configuration admission.
Unvalidated token claims never select a tenant or key scope.

The owned signing-key validation callback also checks the exact configuration
selected by standard validation, including entries in the manager's private
last-known-good cache. Admission retains public-key thumbprints and checks them
again before use. A mutable named-options object cannot introduce an unadmitted
configuration or replace admitted signing material without a closed refusal.
Each validation uses an unexposed standard verification factory, and admitted
keys retain guarded standard factories with custom providers and signature caching
disabled. Factory replacement or mutation is refused before verification;
standard signature and signing-key validation are retained.
The owned algorithm-admission hook repeats key provenance, material and factory
checks before every verifier construction, including historical cached keys.

Standard signature and protocol validation remains in use. Private baseline
parameters require signed **RS256** ID tokens, exact issuer/tenant/client audience,
finite required times and zero clock skew. Critical duplicate claims are refused.
No browser claim, email, display name, `oid`, role or group grants application
access. The only identity key is `("entra", exactIssuer, exactOpaqueSub)`.

Authorization uses `openid` only, code/query response mode, S256 PKCE, state,
correlation and nonce. `select_account` does not promise password re-entry. PKCE
is server-controlled; its verifier may travel through the browser inside protected
state. SaveTokens, UserInfo, token refresh and claim mapping are disabled.

Pushed authorization requests are disabled for the initial Entra profile.
Unexpected nonempty, required or malformed PAR metadata is refused; no PAR
interoperability is claimed. Future PAR support needs an explicit policy increment.
Microsoft documents the initial Entra limitation in its
[OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc?view=aspnetcore-10.0).

Confidential-client redemption uses standard short-lived **PS256** certificate
assertions with a fresh identifier and SHA256 certificate thumbprint. The standard
handler redeems the code and checks the response; no event bypasses redemption.
There is no client-secret or ambient-credential fallback and no retry/resend.

## Flow protection, failures and ownership

Flow state and nonce use a narrow internal bridge through the admitted durable
profile, separated by fixed organization/tenant/client/policy purposes. Existing
session-ticket, cookie-carrier and antiforgery purposes and profile admission are
preserved. State and nonce expire after ten minutes. At most four outstanding
nonce/correlation flows are admitted; cookie/query/redirect/header budgets are
bounded. Transient cookies are host-only, Secure, HttpOnly, Path=/ and SameSite=None.
Application and epoch-bound antiforgery cookies keep their separate existing policy.

The actual generated nonce-cookie name is bound into protected state. A known
failed callback clears only its authenticated matching nonce/correlation cookies;
it does not clear unrelated flows. Invalid state cannot nominate a cleanup target.
The standard nonce scanner sees only the nonce cookie named by authenticated
state; unrelated stale or corrupt nonce cookies cannot poison the current flow.
The original request-cookie collection is restored after protocol work settles.
No public handle, login epoch, raw nonce or token registry is added.

Protocol rejection is `Denied`; request reuse is `UntrustedIssuance`; configuration
conflicts are `ConfigurationFailure`; operational/ring/header failures are
`Unavailable`, through the existing closed exception without provider diagnostics.
HTTP 3xx, 408, 429 and 5xx are unavailable before response-body/protocol handling;
token-endpoint 401/403 are also unavailable. Ordinary 400 `invalid_grant` remains a
denial. Error descriptions are not classifiers. Original request/caller cancellation
is preserved. Failed or started headers do not produce a successful admission or
an undo claim.

The caller owns the durable profile and Entra certificate through runtime shutdown
and in-flight drain. The adapter borrows and never disposes them. Credential,
Data Protection and TLS certificate delivery/rotation remain separate operator
responsibilities. Existing ring admission requires the qualified Linux private
filesystem/statx profile. No Blob repository, Azure Functions, ephemeral disk or
serverless deployment is qualified by this component.

DI owns runtime options/handlers, constrained HTTP and request/retrieval/send work.
`DisposeAsync` stops admission, cancels and drains whole owned operations and HTTP
body reads, then releases owned resources. Standard configuration-manager refresh
uses a private task without a public join/dispose API. Entire owned retrievals and
sends are drained; a later queued refresh fails its closed gate before HTTP.
Remaining private validation/update work uses managed policy/configuration/keys.
It is not claimed to be joined, or to release every captured execution-context
reference synchronously. Borrowed profile/certificates and released HTTP are not
called by that continuation.

## Qualification and later increments

Independent test source uses an internal fixed-policy, owned HTTPS fixture with
real standard certificate/code/PKCE/state/nonce paths. Its only transport seam
connects admitted Microsoft logical URLs to one loopback listener with normal TLS
name checks and borrowed custom-root trust. A bounded method/path-only attempt
ledger exists only for that fixture. There is no production transport override.
Source review does not replace executing the fixture.

Integration must qualify the restored/locked package closure, formatting/build,
registered handlers/defaults and effective policy, real HTTPS protocol/crypto,
hostile metadata/key scope, rollover/last-known-good behavior, denial/expiry,
cancellation/header cleanup and cross-instance protected-flow completion. Mocks
do not qualify a configured live tenant, certificate delivery, consent, Conditional
Access or deployment environment.

P4d2 will own exact Origin/header/antiforgery admission and cache/error policy,
fresh external-binding/principal reads, then trusted session issuance. A fresh
same-actor login must acknowledge old local revocation before storing the new
session; failures do not restore or silently retry the old operation. Binding
absence never provisions an account or grant.

P4d3 will explicitly select the hosted profile and qualify proxy/HTTPS, secrets,
durable ring, listener and diagnostics. Silent handler logging alone does not
suppress IdentityModel global diagnostics, HTTP instrumentation or edge query
logs; the library changes no global logging static. P4e owns Angular navigation,
pending-operation recovery and noncredential identity-change signals. Live tenant
configuration belongs at the later activation gate and does not block dormant
source work. See [Browser sessions](browser-server-sessions.md),
[External identity bindings](external-identity-bindings.md) and
[Server sessions](server-sessions.md) for the separate authority contracts.

The owned `SocketsHttpHandler` fixes `ActivityHeadersPropagator = null` before
first use. In the pinned .NET 10.0.12 implementation, this omits that client's
`DiagnosticsHandler`, its raw request/response/exception `DiagnosticSource`
payloads and associated outgoing request Activities and trace propagation. Other
HTTP EventSource/metrics and global IdentityModel channels remain separate; this
per-client source mechanism is not executed privacy qualification. Approved-sink,
host/edge query and credential privacy controls still require P4d3 qualification.
