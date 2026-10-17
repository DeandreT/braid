# Dormant session-backed catalog boundary

`Weft.Api` offers an explicit provider-neutral catalog/session composition for
[#114](https://github.com/DeandreT/weft/issues/114). It does not register sessions
in the shipped Server or activate Angular sign-in. Source, integration and actual
qualification evidence are recorded separately in the implementation review;
this page reports no test result. Qualified merged
[#112](https://github.com/DeandreT/weft/issues/112) precedes its use.

## Explicit single-provider composition

Call `AddWeftBrowserSessions` and then `AddWeftSessionCatalog` on the same service
collection. The catalog registration takes `CatalogBrowserSessionOptions` with
one nonempty organization ID and the four explicit metadata/ingestion/project
creation/report feature options. It requires one unkeyed browser singleton and
resolves that exact instance lazily when its boundary is constructed. It neither
builds a second provider, replaces the browser nor owns its disposal. The caller
retains the session composition's profile, certificate and pool lifetime duties.

Mixed or duplicate catalog identity profiles fail registration before replacing
the first profile. The existing `AddWeftCatalog` configuration remains an explicit
Development-only identity: hosted environments cannot enable it. The new session
profile installs no Development antiforgery settings. It changes no shipped
Server environment option, listener, default authentication scheme or route.

## Admission before business work

Enabled catalog operations use the browser module's `AdmitRequestAsync`, including
safe/context reads. `ObserveAsync` alone never admits an unsafe catalog operation;
the browser's actual-method gate enforces HTTPS/authority and Origin/epoch-CSRF.
Invalid transport/session/token cannot reach body parsing, business ports,
private storage, native workers or renewal writes. Session-authentication reads
can precede CSRF; they are not catalog authority or business dispatch.

Only a positive private observation with the configured organization supplies
an internal principal/actor and allowed organization. Ambient or transformed
claims, email/provider roles, identity headers and bearer fallback supply no
catalog identity. A missing/refused session yields `identityRejected`/401; CSRF
refusal yields `identityRejected`/403; operational or inconsistent session facts
yield `unavailable`/503. Responses remain no-store, failures omit ETag, and there
are no redirects or private exception details. Cancellation remains cancellation.

Context GET still requires fresh organization admission and, when enabled, the
separate creation-capability observation before it exposes a request token.
`IssueRequestTokenAsync` verifies the same private session actor/organization and
uses the browser module's token operation. Failure cannot produce partial
successful context or a Development identity. The original synchronous token API
remains Development-only; no async operation is synchronously blocked.

Each catalog read/write still checks fresh active principal, organization and
exact project membership/action with the complete resource ancestry. A context,
session, renewed deadline or CSRF token is not a role/capability grant. Explicit
[payload permission](project-payload-permissions.md) remains the current grant
intersected with current project authority; this increment creates no payload
endpoint. Statement/in-flight snapshot limits remain unchanged; admission does
not lock identity or business authority until response/commit.

## Compatibility and follow-ons

The context DTO accepts exactly `development` and `session` with the same required
fields and bounded request token. Mode is chosen internally, never by a request.
Development output, token scheme and local transport remain compatible. No session
handle/digest, binding key, login epoch or provider detail is added to context.

Angular still accepts Development mode only and therefore rejects session mode
until P4e explicitly integrates it. Existing browser context/token state remains
in memory; no browser secrets are persisted. P4e owns401 UX, session-mode guards,
pending-operation invalidation and original-receipt recovery, including choosing
a noncredential signal for same-actor session changes.

P4d owns verified provider ingress, trusted fresh-issuance invocation, public
login/status/logout routes, HTTPS/proxy/host activation and deployment. This
boundary exposes none of those routes and cannot verify Entra claims. It reuses
the [protected adapter](protected-server-sessions.md), browser-module policies and
[session metadata](server-sessions.md); it adds no SQL, grants, key parser,
retention, AI feature or alternate authentication scheme.
