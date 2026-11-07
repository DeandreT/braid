# Organization membership and request-local switching

## Scope and implementation gates

This is the design specification for [#341](https://github.com/DeandreT/weft/issues/341), not a statement of implemented or qualified behavior. Integrate the qualified audit, AI usage and quota baseline, including `0037_audit_log.sql`, `0038_ai_usage.sql` and `0039_ai_quotas.sql`, before changing session or identity code. Source presence is not qualification evidence. [Archive/deletion](archive-deletion-design.md) must preserve this request-local admission boundary.

Reuse global principals and organization memberships. This work does not create organizations, provision tenants, change identity providers, introduce cross-organization business queries or change machine-endpoint authorization. Live Azure qualification requires separate operator authorization.

## Baseline contracts and required changes

| Baseline boundary | Relevant source | Design requirement |
| --- | --- | --- |
| Global principal and `(organization_id, principal_id)` memberships | `migrations/postgres/0001_project_ownership.sql` | Reuse principals, memberships and retained project grants; never clone people per organization. |
| Configured organization in session keys, WST1 and canonical claim; observation joins current binding/principal, not membership | `Weft.Identity/ServerSessionCanonicalTicket.cs`, `ServerSessionTicketCodec.cs`, `Weft.Persistence.Postgres/PostgresServerSessionReader.cs` | Retain that organization as authentication root, independent of business selection. |
| Cookie/bearer catalog scope and bearer audit use configured organization | `Weft.Api/Catalog/CatalogRequestBoundary.BrowserSession.cs`, `Weft.Api/Auth/BearerAccess.cs` | Resolve one route-selected business scope, independently of identity, and audit that scope. |
| Business routes compare route organization with `AllowedOrganizationId` | `Weft.Api/Catalog`, `Weft.Api/Chat`, `Weft.Api/Administration` | Preserve equality/fresh resource checks while changing how allowed scope is established. |
| Unqualified browser context/pages; session acceptance checks fingerprint, not business scope | `apps/web/src/catalog/catalog-api.ts`, `catalog-context.ts`, `catalog-types.ts`, `auth/browser-session-state.ts`, `app.routes.ts` | Add synchronous same-principal organization boundaries independent of session invalidation. |
| CLI caches explicit/root context, with separate `whoami`/`context` behavior and v1 config lacking default organization | `Weft.Cli/Commands/CliInvocation.cs`, `ProfileCommands.cs`, `Configuration/CliConfigurationStore.cs` | Centralize selection/admission/context for every scoped command. |
| Invitation SQL serializes stable identity across organizations; callback targets root and redeemed replay precedes current-activity checks | `0027_administration.sql`, `Weft.Identity/EntraBrowserAuthentication.cs` | Protect separate invitation target and require current identity/admission checks on replay. |
| Exact audit/usage/quota guards, retained identities and checksum/upgrade contracts | `0037`-`0039`, `AuditLogSchemaAdmission`, `AiUsageSchemaAdmission`, `AiQuotaSchemaAdmission`, session upgrade tests | Append migrations and exact compatibility manifests; no presence/minimum-version substitution. |

.NET paths are under `dotnet/src` or `dotnet/apps` as applicable. Supporting contracts: [server sessions](server-sessions.md), [browser server sessions](browser-server-sessions.md), [browser session state](browser-session-state.md), [API authentication](api-authentication.md), [administration](administration.md), AI usage baseline documentation and [CLI](cli.md).

Fixtures and procedures must use `bind-entra-user` with confirmed `tenantId` and `objectId`. Never copy retired provider/issuer/subject binding setup or match email, UPN or pairwise `sub`, including during upgrades/invitation recovery. Retired-binding repair is not an implicit migration in this work.

## Identity, scope and authority

Keep three distinct concepts:

1. **Authenticated identity:** active global principal, current enabled binding and, for browsers, root session/login epoch. Browser and bearer resolve the same fixed-tenant `("entra.oid", tid, oid)` binding.
2. **Selected organization:** one canonical route organization for a business request, not a claim, grant, account switch or session setting.
3. **Business authority:** fresh active membership in that organization followed by existing project roles, payload rights, resource identity and concurrency checks.

Root membership is unnecessary for authentication, discovery or another admitted organization. Zero-admission people can inspect identity, read the non-business guide and complete valid invitation flows. Inactive, unknown or unadmitted selection fails without fallback.

Local organization administrators may suspend their organization's membership, not disable shared global identities. Preserve migration 0027's refusal of principal/binding disable if any other organization membership exists, including inactive retained memberships. Cross-organization global disable remains explicit operator work. Retain fixed-tenant filters, last-administrator protection and existing serialization.

Keep configured root organization, application name, key profile, `ServerSessionKey`, WST1 bytes, canonical claims, binding-version snapshot, login epoch, cookie names and lifetime policy unchanged. Switching creates no session row, changes no cookie and rotates no epoch. Renew/revoke addresses the root namespace. Root-membership suspension is not logout; principal/binding disable prevents fresh authentication everywhere. Existing one-way disable semantics remain.

Singletons may hold immutable configuration, pools and principal-wide rate limiting, never selected organization, admission, tokens or mutable actor state. A private request-local feature binds exact `HttpContext`, identity and scope once; inconsistent rebinding fails closed. No claim transform, `HttpContext.Items` convention or ambient/global selection conveys authority.

## Public API

### Identity and discovery

Add authenticated, bodyless, non-business reads:

| Endpoint | Result |
| --- | --- |
| `GET /api/v1/identity` | `schemaVersion: "weft.identity.v1"`, mode and internal principal ID; Session mode includes auth status's opaque `identityContext`. No selection, business capabilities or request token. |
| `GET /api/v1/organizations?limit=100&cursor=...` | `schemaVersion: "weft.organizations.v1"`, currently active `{ organizationId, name }` admissions in canonical organization-ID order, nullable `nextCursor`. No roles, other members, projects, bindings or credentials. |

Discovery defaults to 50, accepts 1-100, bounds opaque cursors to 512 ASCII characters and complete serialized responses to 128 KiB. Oversize fails, never silently truncates. Names retain the 200-character bound. Cursor binds format/version, principal and last organization ID; malformed/foreign-principal cursors fail. Each page queries fresh state, not a snapshot lease. Do not claim total count or completeness until pagination finishes; revocation may remove entries mid-pagination. No supplied principal, tenant, arbitrary filter or wildcard. Zero membership returns an empty successful page.

Use explicit identity-only metadata/admission, never fabricated business scope. Preserve credential separation, fixed-tenant validation, no CORS, closed errors and principal-wide bearer rate limiting. `/api/auth/*` remains browser-only and rejects Authorization. Identity-only access grants no business access. Help remains authenticated but admission-independent and returns only the embedded guide.

### Qualified context and compatibility

`GET /api/v1/organizations/{organizationId}/context` returns `OrganizationContextV1`:

* `schemaVersion: "weft.organization-context.v1"`;
* `admissionVersion`: positive canonical Int64 decimal string;
* `catalog`: existing `CatalogContextV1` shape, with path-equal `allowedOrganizationId`, global `actorPrincipalId`, current feature/action facts and selected organization name.

Session responses include root-session `identityContext` and a business request token for that organization/incarnation. Bearer responses include neither token nor session fingerprint. Neither lists other memberships. A new envelope avoids changing legacy required fields/meaning silently.

`GET /api/v1/catalog/context` stays root-only with its existing shape. It freshly admits root and issues a root-scoped new business token; it neither selects a sole membership nor remembers selection. Missing root admission yields closed not-found even if other admissions exist. Existing business paths remain authoritative.

Each business route declares exactly one scope. Parse canonical nonzero UUID and bind before body processing/dispatch. Body/resource organization must match the route. No second selector override, mutable server switch endpoint or general selection header. Auth/help/discovery accept no selector. Any legacy business path lacking organization needs explicit root-only mapping, never implicit current selection.

### Errors and contracts

* `400 invalidRequest`: malformed selection/cursor, duplicate/unsupported selector, conflicting scope or invalid credential combination; no existence disclosure.
* `401 identityRejected`: missing/refused credential, preserving bearer challenges.
* `403 identityRejected`: current bearer binding/principal refusal or browser CSRF refusal. Browser identity loss retains existing 401/status classification. Incarnation-token mismatch is CSRF refusal, not successful mutation.
* `404 notFound`: missing/inactive/unadmitted organization or invisible resource, within existing closed error families.
* `429 rateLimited`: existing principal-wide bearer bucket; switching cannot reset it.
* `503 unavailable`: identity/discovery/admission store or profile failure, never fake empty discovery.

All protected identity/discovery/context/business responses are `Cache-Control: no-store`; no shared private cache. Strict DTO readers validate version, mode, principal/scope, bounded names/cursors and decimal-string counters. Names are untrusted display text. Retain each family's error contract without provider/database messages.

Update endpoint metadata, C# DTOs, generated OpenAPI, Angular generated types/runtime readers and CLI `ApiOperations`/drift tests together. Distinguish identity-only/business, cookie CSRF/bearer and auth-action/business-token requirements. Auth-status tokens must not be documented as protection for arbitrary business mutations.

## Restricted discovery persistence

Provision dedicated `weft_organization_discovery` through existing provisioning, with its own protected connection-string file/pool. Do not reuse session writer, runtime, administration or migrator credentials.

Grant only schema usage and EXECUTE on one bounded discovery function. No direct table/column reads, writes, CREATE/TEMP, role membership, grant option, BYPASSRLS or generic query helper. Revoke PUBLIC execute. The security-definer function has exact owner, safe fixed search path and static SQL. Verify expected session user and current binding ID/version/principal tuple, join active principal/memberships and project only organization ID/name. Never use a magic `weft.organization_id` or relax business RLS.

The web boundary supplies the tuple from fresh private cookie observation or verified bearer identity, never HTTP. Database checks establish consistency/current identity, not independent credential possession; the trusted application credential presents already verified identity.

The adapter admits exact function definition, owner, ACL, source schema and login attributes before use. Apply statement/lock timeouts, cancellation and clean pooled connection state. Duplicate/foreign/invalid/oversize results and malformed cursors are integrity/unavailable errors. No privilege broadening or alternate-login fallback. Include negative-grant/function-tamper fixtures.

## Business admission and CSRF

Refactor orchestration without changing session key/identity:

1. Select allowed credential from route metadata, require exactly one, resolve fresh root-session or bearer identity, preserving origin/cookie/cancellation/binding-version/lifetime checks. **Immediately after bearer identity resolution, apply principal-wide rate limiting before discovery, organization admission or business authorization.** Unknown, inactive and unadmitted selections consume the same bucket.
2. Parse/bind route scope privately, then read current active admission and `admissionVersion`. Discovery never substitutes for admission.
3. Before unsafe cookie body consumption, renewal effects or dispatch, validate standard antiforgery cookie/header and exact Origin with business additional data binding root, login epoch, selected organization and incarnation. Retain existing unsafe-method refusal policy.
4. For admitted bearer mutations, durably record the attempt in the selected organization with original binding/client before dispatch. Failed/unacknowledged audit prevents dispatch. No quota acquisition or business identity allocation before admission/audit success.
5. Execute with `ActorContext(globalPrincipal)` and selected organization, preserving fresh project roles, payload, ownership, ETag, idempotency and resource checks. Transactions, storage keys, outbox and returned DTOs share that exact scope.

Use `weft:business-csrf:2` for canonical unambiguous business additional data, preserving standard antiforgery/principal binding. Issuance requires private request-local admitted scope, not arbitrary organization arguments or claims. Exact trusted metadata/private state selects auth-root versus business purpose; missing business scope refuses without root-token fallback. A tokens cannot authorize B; separate tabs may retain A/B tokens with one cookie. Tokens confer no membership/project rights.

Keep `/api/auth/session` root-epoch transport token and existing separate auth-action purpose for login/logout/forget-local. Neither protects business routes. Auth lifecycle stays acknowledged and independent of tab selection. Business tokens cannot replace root/auth-action tokens.

Old root/epoch-only business tokens are refused after rollout. Root clients recover by explicit legacy-context read, never transparent mutation replay. Existing cookies, sessions and auth-status response remain usable without session rewrite or blanket logout.

## Membership incarnation

Active checks alone cannot distinguish suspension/reactivation. Add retained `organization_admission_state`, keyed `(organization_id, principal_id)`, with positive Int64 `version` and restrictive membership FK. Backfill all retained memberships, active/inactive, at 1. An owner-maintained trigger creates new state and increments exactly once per `active` change, not no-op update. Refuse overflow, state reset/deletion and membership deletion losing incarnation. Provisioning/administration/invitations all pass through the trigger.

The separate table preserves existing administration `to_jsonb(membership)` output. Use exact-organization RLS and narrowly scoped runtime reads. HTTP cannot update it. Discovery can access it only inside its function if needed and never expose it. Missing/inconsistent state is integrity failure, not invented version 1.

Changed `admissionVersion` ends a browser lineage even with unchanged principal/epoch/organization. Old unsafe tokens remain invalid after reactivation. Recovery needs fresh context/resource authority and original ownership. Retain project memberships/payload grants as administration specifies; reactivation can restore retained rights, not create new grants. Project/role changes retain fresh checks and ETags.

No instantaneous revocation promise for already authorized work/displayed data in disconnected tabs. New requests and continuing chat/tool checks observe current authority at established DB boundaries; later changes cannot undo commits. Focus/action refresh and observed incarnation changes clear stale presentation without claiming unqualified push revocation.

## Browser selection and private work

### Routes

The header switcher lists bounded discovered admissions and requires explicit selection. Store selection in tab memory plus authoritative URL, not shared cookies, localStorage or server preference. Tabs independently use A/B under one session.

* Preserve `/catalog/organizations/:organizationId/...` resource links; add `/catalog/organizations/:organizationId/search`.
* Qualify standalone routes: `/organizations/:organizationId/chat`, `/organizations/:organizationId/chat/resources`, `/organizations/:organizationId/notifications`, `/organizations/:organizationId/administration`, `/organizations/:organizationId/audit`, `/organizations/:organizationId/usage`, `/organizations/:organizationId/quotas`.
* New links/citations/notification targets/breadcrumbs retain original organization. Repeated project/mapping/turn IDs across organizations remain distinct.
* Unqualified standalone routes are explicitly root-only, never selected-tab aliases. Already scoped legacy links, including chat resource queries, retain/validate their exact scope, not a root relabel.
* `/catalog` is the chooser: zero admissions shows no workspace, one may navigate to its qualified home, several require choice. An explicit inaccessible URL remains inaccessible despite another sole admission.
* Switch to destination home by default; never copy A project/mapping/cursor/conversation/report IDs into B.

Update `BrowserReturnPath`, `apps/web/src/auth/return-path.ts`, shared vectors and protected-flow limits for exact routes. Preserve query/fragment allowlists and report/citation identity. New prefixes do not permit arbitrary paths, redirects or double-decoding.

### Two independent generations

Keep `BrowserSessionState` for authentication observation and add organization selection/admission generation to catalog/workspace coordination. Capture every protected operation's:

`(sessionGeneration, organizationGeneration, globalPrincipal, organizationId, admissionVersion, completeResourceScope, requestIdentity)`.

Fence success, error, cancellation, finalize, retries, loading flags, focus, downloads, SSE and recovery acknowledgement with that tuple. `identityContext` intentionally stays unchanged across A/B. Late A 401/403 cannot use unqualified `CatalogContextStore.reject` to clear B on matching root fingerprint. Rejection/classification must check captured scope/generations first and ignore obsolete work before presentation or classification changes.

On switch, Back/Forward or reused-component route change:

1. Resolve destination and use one leave decision for unsent/uncertain work, quota/admin forms, upload bytes, chat and private invitation links. Cancel preserves URL/selection/workspace/generation. Hidden-workspace prompts expose no private owner/draft.
2. After acceptance, synchronously advance organization generation, clear old context/token/lineage and hide/inert protected A views before destination presentation. Settle waiting A requests as not dispatched, cancel owned reads/followers and clear queued focus. Abort never proves mutation rollback.
3. Observe fresh identity; fetch/validate qualified destination context, identity/scope/incarnation; then obtain complete child resource admission. Do not reveal B using A context or A content beneath B heading.
4. Publish readiness only if captured generations still match. Refusal leaves scoped unavailable view and chooser route, without fallback/relabeling.

Routine same-owner/incarnation refresh preserves forms/focus/scroll while dispatch waits. Changed incarnation/principal/epoch/organization is not routine. Organization failures invalidate affected workspace; observed authentication boundaries invalidate all protected work in that tab. Auth actions affect the shared cookie; other tabs detect change on next focus/action without changing their organization preference.

### Recovery and chat

Private work belongs to `(mode, principal, organization, full resource/request identity)`. Epoch/incarnation are freshness fences, not new business idempotency keys. Only the original owner with fresh complete authority may explicitly inspect/recover uncertain work after login/reactivation.

No durable drafts or cross-organization draft registry. Accepted route loss follows existing discard/suspend rules. Retained uncertain work remains scoped/private; drafts, bytes, receipts, ETags, invitation secrets and quota edits cannot render in or retarget to B. Matching reads do not acknowledge uncertain mutations. Never auto-resend after switch/refresh/tab return/renewed authority.

Switch detaches chat followers and fences events without automatically cancelling server turns. Explicit Cancel uses original A conversation/turn/request and fresh A admission. Return may read/follow under fresh authority but never recreate turns. Server context, focus, citations, tools, storage and reservations stay in A; tools use fresh checks, never singleton selection.

## Invitations

New private links: `/organizations/{targetOrganizationId}/invitations#<secret>`. Organization is public routing metadata; secret stays in fragment/private memory until deliberate login. Remove fragments from visible history without logs, telemetry, localStorage, return URLs or discovery retention.

Login accepts optional canonical `X-Weft-Invitation-Organization` paired with `X-Weft-Invitation`. Target without secret is invalid. Legacy secret-only login explicitly targets root. Target is independent of `X-Weft-Return-To`; neither grants authority. Accept the pair only in the existing login CSRF/exact-origin boundary.

Protect target/secret in exact Entra flow state with correlation, nonce and PKCE. Callback validates root flow/profile, then redeems for protected target using verified fixed-tenant `(tid, oid)`. Invitation cannot select tenant/provider. Preserve flow size limits with versioned state distinguishing root/target. Admit in-flight legacy state only as root-targeted until its existing expiry, never reinterpret later selection.

Redemption preserves organization/stable-identity locks, binding/principal concurrency, current inviter org/project authority, expiry, expected object, exact grants and atomic audit. Reuse enabled global binding; never replace principals, duplicate bindings or silently restore disabled identities, suspended admissions or removed/conflicting project memberships.

Fix redeemed replay's early return: historical consumption is not fresh admission. Acknowledge original redemption only after current matching stable binding/principal and permitted admission/grant checks. Replay cannot revive anything, add grants or issue usable sessions for disabled identities. Wrong identity/target fails. Refusal rolls back partial principal/binding creation. Add tests before changing the branch.

After acknowledged redemption/root-session issuance, navigate to qualified target and fetch fresh context. Return links elsewhere require separate admission. Lost callback/cookie acknowledgement cannot justify another invitation, identity or automatic mutation retry.

## CLI selection and recovery

Resolve one `SelectedOrganization` before each business invocation:

1. Explicit canonical nonzero `--organization`.
2. Otherwise the selected profile's explicit saved default.
3. Otherwise complete discovery and select only exactly one current admission.
4. Zero/multiple stops before business work with listing/selection guidance. Noninteractive/JSON never prompts or picks first/root. Interactive users can choose explicitly and invoke again.

Revoked explicit/saved selection fails qualified context even if an alternative is sole; never silently repair profiles. `weft organizations list` requires no selection. Profile/login/logout need no business admission; guide is identity-only. All scoped commands, including `whoami`/`context`, use the same qualified context.

Config v2 adds optional `organizationId`, with `weft profile set ... --organization ID` and `weft profile clear-organization NAME`. Read valid v1 as no default without rewriting/inference. Write v2 only on explicit profile changes, preserving profiles, selection, server/tenant/client/scope and protected token-cache identities. Retain strict duplicate/unknown checks and owner-only atomic writes. Old binaries may refuse v2; never promise reverse compatibility or silently downgrade.

Capture server origin, profile/account/principal and organization for the invocation. Eligible-read token renewal must keep principal; account changes fail. Context, pagination, polling, download, SSE reconnect, Ctrl+C and explicit recovery preserve scope. Returned mismatch is invalid response, never fallback. Do not follow arbitrary server URLs with credentials.

Keep bounded retries for eligible reads only. No mutation retry after 401/403/429/503, transport loss or profile change. Recovery uses original organization/request/payload-or-digest/reviewed ETag. New features require discovery/qualified context and clearly refuse unsupported servers rather than substituting root. Old clients retain root-only legacy behavior.

## Audit, usage and quotas

Qualify/integrate this baseline before switching code to avoid intermediate accounting loss:

* Bearer attempts use private selected scope, not `BearerAccessOptions.OrganizationId`, retaining principal/binding/client/route-resource metadata. `record_api_request` stays scoped with no new table-read grant; acknowledged attempt is not successful execution.
* Migration 0037 remains append-only/source-deduplicated with current org/project-admin visibility. In-flight A work remains A, including its audit. Quota administration keeps its source audit.
* Migration 0038 retains `(organization, principal, turn)`, original focus, admission time, profile/version, independent known/unknown counts, terminal-round deduplication and coverage. Erasure cannot reset usage.
* Migration 0039 policies, reservations and admission locks remain per organization. Principal quota is `(organization, principal)`, not global; bearer rate limiting remains principal-wide. Switching transfers no charges/reservations.
* Preserve atomic whole-turn reservation/insertion, original-envelope/profile proof, post-lock deadlines and exact replay. Unknown/interrupted/cancelled work retains capacity until the existing acknowledged-settlement rule permits release. Context/discovery cannot settle turns.
* Filters, pagination, CSV exports, quota ETags and form owners bind selected organization and complete query scope. A pages/cursors/ETags/exports cannot populate B. Public Int64 usage/quota/incarnation values are decimal strings.

## Migration and compatibility

Never edit, renumber or replace existing migrations. Allocate the next actual version after the qualified integration head is established. This specification reserves no migration number.

Add discovery function and admission-incarnation table/indexes/trigger with bounded backfill; replace only required invitation replay function. Narrowly extend runtime admission reads. Provision discovery login first; missing/misconfigured login refuses transactionally without recording migration. Never rewrite sessions/protected bytes/bindings/principals/membership activity/project grants/chat identities/accounting as convenience backfill.

Exact compatibility requirements:

1. Preserve every checksum/journal row, 0037 event identity/deduplication, 0038 `complete_since`/known/unknown counters and 0039 policy versions/reservations/unsettled charges/proofs.
2. Extend `AdministrationRoleGuard`, catalog/authority captures, discovery guard and exact source/ACL/trigger manifests. Preserve exact intentionally supported historical prefixes; admit new contracts only with exact migration. Audit/usage/quota definitions/grants remain checked.
3. Update embedded-migration assertions and `PostgresServerSessionUpgradeTests`/`PostgresServerSessionUpgradeConservation`, including explicit permitted state/trigger/grant/function deltas. Do not merely increment counts or skip old tables; retain existing relation/column/data/ACL comparisons except reviewed additions.
4. Keep exact root-session schema/key-profile admission. Session pools gain no membership/artifact/discovery privileges. Reset prevents A/B identity/scope leaks after success, exception, cancellation and timeout.

### Populated upgrade matrix

| Starting point | Required conservation |
| --- | --- |
| Through 36 | Apply 37/38/39 in order, then switching. Compare known-time audit backfill/history; preserve retained-versus-erased chat coverage, with no fabricated pre-coverage quota capacity. |
| Through 37 | Preserve audit rows/source identities; usage backfill once, then quotas/admission state. |
| Through 38 | Preserve coverage time/usage/failures; quotas add no invented historical reservations, then admission state. |
| Through 39 | Byte/value conservation of audit/usage/policy/reservations; only designed discovery/admission delta. |
| Full schema rerun/no-op | Journal/data unchanged; no duplicate events/backfill or incarnation reset. |

Populate two organizations with shared principal, B-only/no-root principal, inactive memberships, active/disabled bindings, retained retired binding, live/expired/revoked root sessions, invitations in all terminal states, overlapping resource IDs, erased private chat and unsettled reservations. Compare private fixture facts without exposing credentials/payloads. Retain older identity/session prefix tests.

Mixed-version contract: existing cookies and root clients remain meaningful; stale business tokens need refresh; new clients need new endpoints. Exact-guard old binaries are not presumed compatible. Qualify coordinated rollout or an explicit compatible bridge before activation; never disable guards for rolling deployment.

Disabled auth/catalog stays disabled. Development stays loopback-only, fixed-principal, single-organization with existing transport/CSRF rules. Discovery exposes at most freshly admitted configured organization; qualified context refuses others. Do not enumerate memberships, enable bearer or infer Session from endpoint failure. Update Development fixtures and explicit browser mode handling.

Backout uses only an application binary qualified against retained schema. No database downgrade, state/history deletion, incarnation reset, mutation replay or session move. An incompatible old binary is not rollback; keep switching unavailable until qualified application recovery.

## Required qualification

These are acceptance tests for the exact integrated implementation, not completed results.

### HTTP and persistence

* Same cookie/bearer principal in A/B; B-only works without root; zero admissions can discover/help but not use business routes. Unknown/inactive/unadmitted/malformed selection never falls back.
* Concurrent pooled/singleton A/B requests return/audit only own scope. Repeated IDs, stale cursors, path/body conflicts, mixed credentials, malicious selectors and tampered discovery fail closed. Denied organization requests hit shared rate limiting before admission-store work; no expanded privileges.
* Existing root cookie survives upgrade without ticket/epoch rewrite. Renew/logout remain root. Root suspension leaves B usable; global disable blocks both. Retired bindings are not guessed/migrated.
* Local administrators cannot disable identities shared with active or inactive other memberships. Selected suspension honors last-administrator rules. Disabled/Development modes retain boundaries.
* A token fails B mutation before body/dispatch. Old epoch/incarnation, auth-status and auth-action tokens fail business. A/B tokens coexist; business tokens fail auth actions.
* Suspend/reactivate A with unchanged principal/epoch: incarnation advances twice, old token remains invalid, B works, fresh A context/authority permits explicit owner recovery. Test no-ops, overflow, owner writes and concurrency.
* Invitations: new/legacy links/state, tampering, wrong tenant/object, concurrent same-identity A/B redemption, expiry/revocation, disabled identity, suspended admission, inviter changes, project conflicts, redeemed replay and lost acknowledgement. No replay bypass or duplicate principal.

### Browser

* Real two-tab one-cookie A/B, independent URL/token; cross-tab login/logout observed on focus/action.
* Delay A success/401/403/404/network/finalize/focus/download/SSE until B ready. None clears B, exposes A, alters B busy/errors or launches stale classification. Include A-B-A, rapid switching, component reuse, history navigation and incarnation changes.
* Cover catalog and every standalone page: administration, audit, usage, quotas, notifications, workspace/revision chat, search/citations, cancelled and hidden-workspace leave decisions.
* Exercise drafts, uncertain/in-flight writes, uploads, receipts, private invitations and quota/admin forms. Fresh epoch/incarnation requires authority/explicit recovery; no cross-scope presentation/adoption/resend.
* Detached A turns remain A under current server authority. Cancellation/reconnect retain A. Tools/citations/history/usage never cross scopes.

### CLI, accounting and compatibility

* Explicit/saved/sole/multiple/zero selection, revoked defaults, `whoami`/`context` parity and complete pagination before sole-admission decisions; no root/first-page guessing.
* v1 read/v2 explicit write, unknown/duplicate rejection, private/atomic writes and token-cache conservation. Unsupported server/account change refuses before mutations.
* Pagination/polling/read retry/refresh/SSE/Ctrl+C/recovery retain principal/scope; no mutation retries.
* Audit uses corresponding A/B scope; quotas separate organization/principal while rate limit stays shared. Switching/erasure/suspend/reactivate/uncertain completion never changes ownership or settlement.
* Run populated migrations, existing identity/session/administration suites, discovery negative-grant fixtures, browser isolation, CLI drift and generated-contract parity. Distinguish offline fixtures from later provider/cloud qualification.

## Implementation order and remaining decisions

1. Integrate the qualified audit/usage/quota baseline.
2. Reserve actual migration number; implement additive persistence, exact guards and populated conservation before activating admission.
3. Add identity/discovery/context, scoped CSRF, selected bearer audit and protected invitation target/replay, preserving root compatibility and existing cookies.
4. Add URL/tab generations and recovery across all protected pages, then CLI config/selection and generated contracts/help.
5. Qualify the integrated head, including old-client/root compatibility and coordinated rollout or bridge; enable only after these gates pass.

URL selection, root-only legacy aliases, explicit CLI defaults, separate discovery privileges and retained incarnations are the selected design, not implementation guesses. Remaining gates are qualified baseline integration and actual migration numbering. Release ownership must select and qualify the coordinated rollout/bridge strategy. No live-cloud or mixed-version success is assumed.
