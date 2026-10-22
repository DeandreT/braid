# Session-safe browser state

The Angular catalog observes the fixed authentication status protocol before
requesting catalog authority. It does not infer Development mode from a missing
route or failed request. The status protocol and host profiles are described in
[Authentication HTTP](browser-authentication-http.md) and
[Session host operations](session-host.md).

## Observation and authority

`BrowserSessionApi` sends a fresh bodyless `GET /api/auth/session` with
`X-Weft-Session: 1`. It accepts only the complete versioned status schema, including
mode and authenticated-token/fingerprint coherence. Invalid responses, timeouts,
missing endpoints and network errors produce an unavailable state. Only overlapping
pending observations share a request; a settled positive status is not a reusable
authority cache.

`BrowserSessionState` retains the current observation privately. Its public view
contains categorical state rather than authentication tokens or the identity
fingerprint. Status success does not grant organization, project, mapping or
mutation permission. `CatalogContextStore` obtains fresh status before its normal
context request; a Session context must match the observed `identityContext`.
Development contexts retain their separate contract.

Every catalog action continues to use its actual catalog-context request token.
The status authentication-action and session-epoch tokens are not substitutes
for catalog CSRF tokens. Browser admission guards fence stale callbacks; the
server still authorizes each operation independently.

## Checking and identity boundaries

A safe observation hides and inerts the mounted protected workspace and blocks
new dispatches. It does not destroy private forms, cancel an already-sent mutation,
or reconstruct its request. An unchanged positive observation does not invalidate
that original work. Canceling the last pending observer keeps the workspace
unavailable until another explicit observation, without claiming that an
in-flight mutation was undone.

Creation, upload and append completion focus waits until the protected workspace is visible
again. A focus request is consumed only after its rendered target receives focus;
later safe checks do not replay it. Identity invalidation cancels queued focus
along with the existing generation-bound presentation callbacks.

A changed, negative or failed observation first advances the boundary generation,
removes old acceptance/visibility, and synchronously notifies consumers. They
clear protected ready views, access facts and reports, fence late callbacks,
cancel owned pending reads and suspend original mutation bookkeeping before the
replacement categorical view appears. Queued focus refreshes are cleared at this
true boundary; later ordinary focus events retain their normal admission flow.

Current Session catalog or report requests rejected with HTTP401/403 use
`rejectProtectedRequest` to clear the protected boundary synchronously before
starting one fresh, status-only observation. In-flight rejection classification
is coalesced, and captured boundary generations fence obsolete API failures. The
retained private status and any supplied admitted context establish request
ownership even while a safe check temporarily hides the public workspace.
The session service owns this eight-second-bounded observation independently of
the rejected consumer, so clearing an editor or canceling its read cannot cancel
classification.
Overlapping explicit observers share the pending status request but retain their
own subscriptions. A superseding fresh observation, explicit invalidation, another
true boundary or service destruction releases the service-owned subscription;
other observers are not forcibly unsubscribed and obsolete results cannot publish.

Classification publishes only the observed identity category. An authenticated
result does not restore catalog context, resource DTOs, reports, catalog request
tokens or private recovery presentation. A negative result retains its exact status
category; a failed check remains unavailable without clearing the same boundary again.
Classification never reloads catalog authority, retries a business request or
performs an authentication action. Explicit invalidation and context/status
mismatch retain their separate unavailable behavior without automatic observation.

Generic workspace Retry obtains status and organization context only. The child
screen must subsequently obtain its own full resource admission through its
existing Refresh, Retry or explicit action. A context-only Retry cannot revive an
old child-ready graph or dispatch a mutation. Pending presentation is mount-only;
it never relaxes current action/capability checks.

Existing guarded editor/upload routes use a fixed browser-owned leave decision
when the workspace is hidden. It exposes no retained owner, draft or receipt and
warns that leaving can lose private recovery information without undoing a sent
request. Cancel keeps the route; accepting permits the existing route-loss cleanup.
The ordinary in-page decisions remain in use while workspace access is visible.

## Private recovery ownership

Original work belongs to its mode, organization, principal and complete resource
scope. The login epoch is not part of the original business-request identity.
A fresh same-owner login can recover that work only after fresh complete authority
and explicit review/action. A foreign owner or mode cannot view, adopt, retarget,
resend or overwrite it.

Project creation, metadata edits, native upload and revision append retain their
existing request keys, raw drafts or native bytes, digests, receipts, uncertainty,
ETags and reviewed concurrency conditions in private tab memory. An unknown result
stays unknown: neither cancellation nor a matching later GET acknowledges an
unconfirmed mutation. Recovery uses the existing operation-specific protocol,
not an automatic resend.

On a confirmed metadata-owner change, an unsent draft with no retained request
or uncertainty is cleared, and the new owner needs fresh resource admission to
start an editor. A retained PATCH attempt or uncertainty instead suspends its
private baseline and raw draft. Public editor/access/comparison displays are
cleared. Restoration requires the original owner, fresh mapping admission and
explicit Review latest. A failed or negative session observation alone is not a
confirmed new owner. Existing explicit replacement/abandonment and accepted route
loss retain their normal memory boundaries.

Revision-page workflow panels (regression tests, builds and chat) are child
views that a boundary or a catalog refresh unmounts. Their unsent drafts and
uncertain requests live in page-scoped recovery providers rather than in the
panel. A boundary or refresh suspends that work. Only a fresh catalog context
whose owner (mode, principal, organization and Session identity context)
differs discards it, as does another revision, explicit abandonment or leaving
the page. The panel restores it to the same owner only after its own fresh
admission.

There is no durable draft storage, cross-tab registry, account-sharing mechanism,
provider SDK, automatic mutation retry or sign-in/out control in this state
increment. Sign-in controls compose this boundary separately.

## Isolated browser qualification

The normal shipped Angular entry remains `src/main.ts`. The separately selected
`browser-session-probe` build imports the actual application components/services
through a test-only entry and produces a distinct, nonshipped publication.
`npm run build:browser-session-probe` builds it; `npm run test:browser-session`
selects its controlled suite on an alternate virtual display.

The controlled owner requires independently admitted, separate immutable app and
probe roots with complete file byte/hash inventories in a private configuration.
Its synthetic API refusals never fall back to the development backend. Probe
controls and source must not enter the ordinary Angular output or Server
`wwwroot`. These browser fixtures do not qualify real backend authorization,
Entra, cookie enforcement or deployment.

The packaged persistence fixtures admit the exact same-origin, bodyless session
status GET with its observation marker through their existing real GET delivery
and capture paths. They reject query variants, authentication actions and supplied
authority or mutation headers. The host supplies the status response; catalog
context, resource authority and business-operation assertions remain separate.

Controls are disabled while an admission, submission or access read is in flight,
and the zoneless application renders that state after the triggering event. Browser
tests therefore observe completion on the wire (the answered context or resource
read) before asserting control state or changing fixture authority, rather than
inferring it from a button that is enabled or disabled at that moment.
