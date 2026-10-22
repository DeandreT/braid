# Weft delivery plan

Each increment links its tracking issue, implementing pull requests and contract
documentation. The early increments are recorded in
[PR #6](https://github.com/DeandreT/weft/pull/6) and
[PR #10](https://github.com/DeandreT/weft/pull/10) and
[PR #11](https://github.com/DeandreT/weft/pull/11) and
[PR #12](https://github.com/DeandreT/weft/pull/12) and
[PR #21](https://github.com/DeandreT/weft/pull/21) and
[PR #22](https://github.com/DeandreT/weft/pull/22) and
[PR #26](https://github.com/DeandreT/weft/pull/26) and
[PR #27](https://github.com/DeandreT/weft/pull/27) and
[PR #33](https://github.com/DeandreT/weft/pull/33) and
[PR #38](https://github.com/DeandreT/weft/pull/38) and
[PR #40](https://github.com/DeandreT/weft/pull/40) and
[PR #45](https://github.com/DeandreT/weft/pull/45) and
[PR #48](https://github.com/DeandreT/weft/pull/48).
P3d follows the focused [catalog API plan](docs/catalog-api-plan.md).
The remaining sequence follows below.

The architecture review is tracked in [#1](https://github.com/DeandreT/weft/issues/1).
The first application increment, P0, is tracked in
[#2](https://github.com/DeandreT/weft/issues/2).
P1's mapping contracts are tracked in [#5](https://github.com/DeandreT/weft/issues/5).

## Delivery rules

Each row is one focused, independently reviewable PR. Use `feat/<description>`
branches and keep each increment buildable. Merge qualified increments before
their dependents merge. Independent source work may proceed in isolated
worktrees against already qualified contracts while a preceding increment
finishes verification. A feature's written contract, implementation, and
qualification are separate statuses.

Before implementation, open a scoped issue with source ownership, exclusions,
dependencies, and acceptance checks. Record the implementing PR and verification
evidence. The documentation PR does not itself qualify an application feature.

## First release sequence

| ID                                                        | PR scope / source ownership                                                                                                        | Depends on                   | Acceptance gate                                                                                                                                                                                                       | Excludes                                                   |
| --------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- | ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| P0                                                        | Buildable ASP.NET Core/API and Angular shell; `dotnet/apps/Weft.Server`, `dotnet/src/Weft.Api`, `apps/web`, CI                     | Architecture review          | Clean checkout builds; API health and browser shell work; Package metadata and lockfiles are consistent                                                                                                               | Engine integration, production hosting                     |
| P1                                                        | Versioned domain/API contracts and synthetic mapping projection; `dotnet/src/Weft.Domain`, `dotnet/src/Weft.Contracts`, API schema | P0                           | Generated browser types; deterministic fixture projection; no Ferrule/vendor types in public contracts                                                                                                                | Universal execution language                               |
| P2a / [#7](https://github.com/DeandreT/weft/issues/7)     | Bounded Ferrule codec/inspection/validation worker and C# client; `engines/ferrule-host`, `Weft.MappingBackends`                   | P1                           | Pinned native codec/validator; exact framed bytes; typed owners; real isolation, timeout and cancellation checks                                                                                                      | Projection, viewer, uploads, preview                       |
| P2b / [#8](https://github.com/DeandreT/weft/issues/8)     | Native projection walker and adapter qualification                                                                                 | P2a                          | Deterministic fields/rules/controls/named boundaries; resolvable locators; independent expected examples and explicit gaps                                                                                            | Viewer, evaluation, editing                                |
| P2c / [#9](https://github.com/DeandreT/weft/issues/9)     | Angular mapping viewer and development native examples                                                                             | P2b                          | Search/detail/lineage/validation navigation, controls and coverage visible; keyboard and responsive browser checks                                                                                                    | Catalog, auth, chat, preview                               |
| P3a / [#13](https://github.com/DeandreT/weft/issues/13)   | Project ownership, application access/read contracts and scoped PostgreSQL migrations                                              | P2c                          | Actual PostgreSQL roles, scope, revocation, migration concurrency, pool reuse and restart checks                                                                                                                      | Mapping/revision persistence, API, identity                |
| P3b / [#14](https://github.com/DeandreT/weft/issues/14)   | Private storage contract and local adapter                                                                                         | P3a                          | Create-only publication, exact bounded reads, scoped generated references and restart                                                                                                                                 | Ingestion, Azure transport                                 |
| P3c1 / [#23](https://github.com/DeandreT/weft/issues/23)  | Scoped ingestion metadata, restricted writer pool and atomic publication                                                           | P3b                          | Actual PostgreSQL grants, ancestry, receipts, immutable facts, leases and committed order; synthetic admission evidence                                                                                               | Storage/native workflow, HTTP/Angular                      |
| P3c2 / [#24](https://github.com/DeandreT/weft/issues/24)  | Exact native revision ingestion and process recovery                                                                               | P3c1, P2 native adapters     | Actual PostgreSQL/storage/worker; exact bytes; admission; idempotency, revocation and crash recovery                                                                                                                  | HTTP/Angular catalog, cache                                |
| P3d1 / [#28](https://github.com/DeandreT/weft/issues/28)  | Scoped catalog/history reads, generated contracts and explicit Development identity/request boundary                               | Both P3c increments          | Actual API/database; ancestry, narrow reader grants, bounded pagination, closed local identity and contract precision                                                                                                 | Metadata writes, upload HTTP, Entra, Angular catalog       |
| P3d2 / [#29](https://github.com/DeandreT/weft/issues/29)  | Conditional mapping metadata writer/API and strong metadata ETag                                                                   | P3d1                         | Actual narrow grants/transition guard; conditional edits, rename/append races and mutation protection                                                                                                                 | Native replacement, raw upload HTTP                        |
| P3d3 / [#30](https://github.com/DeandreT/weft/issues/30)  | Raw native upload, safe own receipt and explicit resume HTTP API                                                                   | P3d2, C2                     | Actual API/database/storage/worker; bounded exact bytes, conflicts, cancellation, API restart and mutation protection                                                                                                 | Background jobs, native download, Angular catalog          |
| P3d4a / [#39](https://github.com/DeandreT/weft/issues/39) | Explicit creation capability, restricted atomic creator persistence and immutable initial administrator receipt                    | P3d3                         | Actual restricted login, atomic scoped creation, same-key contention/replay, authority locks and no regrant after revocation                                                                                          | HTTP, host composition, organization/member administration |
| P3d4b / [#31](https://github.com/DeandreT/weft/issues/31) | Project creation HTTP routes, fresh organization actions and host composition                                                      | P3d4a                        | Actual create -> catalog read -> native upload; durable replay and mutation protection                                                                                                                                | Organization/member administration                         |
| P3e1 / [#49](https://github.com/DeandreT/weft/issues/49)  | Complete report keys, policy, scoped metadata ports, PostgreSQL 0006 and dedicated report roles                                    | Qualified P3d, P2c contracts | Actual narrow grants, native-source binding, leases, fresh authority after waits, immutable results, repair and restart; explicitly synthetic completion facts                                                        | Codec, native/storage workflow, HTTP, viewer changes       |
| P3e2 / [#50](https://github.com/DeandreT/weft/issues/50)  | Verified stored report codec and native derivation workflow                                                                        | Qualified P3e1               | Actual store/worker/database; cache hits, complete keys, strict integrity and provenance, cancellation, recovery and repair                                                                                           | HTTP, cleanup, viewer changes                              |
| P3e3 / [#51](https://github.com/DeandreT/weft/issues/51)  | Scoped persisted-report API, explicit host composition and generated contracts                                                     | Qualified P3e2               | Actual current/retained HTTP reports, bounded responses, no-work reads, request admission and restart                                                                                                                 | Cleanup execution, viewer changes                          |
| P3f / [#18](https://github.com/DeandreT/weft/issues/18)   | Reservation-aware orphan and derived-object maintenance                                                                            | P3e                          | Bounded leases; publication/deletion races; crash recovery; referenced native revisions retained                                                                                                                      | Native-history purge, messaging                            |
| P3g / [#19](https://github.com/DeandreT/weft/issues/19)   | Angular catalog, upload and immutable revision history                                                                             | P3f, P2c viewer              | Actual persistent API/browser flows; reopen old revision after restart; keyboard/mobile checks                                                                                                                        | Authentication, chat, preview                              |
| P4                                                        | Entra session login and application permissions; identity/policy modules                                                           | P3g                          | Tenant/issuer/session validation; role and project access checks; CSRF and logout; separate payload grant; hosted config rejects development identity                                                                 | Other providers, Azure directory administration            |
| P5                                                        | Model-provider contract, configuration, orchestration, bounded streaming; `dotnet/src/Weft.Ai`                                     | P1, P4                       | Two protocol fixture adapters; one configured provider integration; authenticated stream, cancellation, usage/limit/error handling; server-side secrets                                                               | Mapping action tools, unqualified provider capabilities    |
| P6                                                        | Mapping-grounded chat window and authorized read tools; `apps/web`, AI tool registry                                               | P2, P3, P5                   | Revision-pinned answers/citations; lineage and validation tools; same interface for qualified hosted/local profiles; no mutation or payload tools                                                                     | Autonomous execution/editing, vector database              |
| P7                                                        | Bounded JSON sample preview, Wolverine durability, and worker lifecycle; `dotnet/apps/Weft.Worker`, preview UI/API                 | P2, P3, P4                   | Outputs match upstream host API; parameters/target preflight; worker timeout/cancellation; durable job replay with idempotent output publication; input/output limits; no external writes or inherited network access | Stateful connectors, generated deployment                  |
| P8                                                        | First Azure-hosted application environment; `deploy/azure`, runtime packaging                                                      | P4, P6, P7                   | Reproducible deployment; private storage and workload identities; end-to-end viewer/chat/preview; instance restart/scale recovery                                                                                     | Publishing customer mappings as separate functions         |

P3 uses the ordered ownership/storage/ingestion/API/cache/maintenance/browser
sequence in [the catalog persistence plan](docs/catalog-persistence-plan.md).
P3c's two focused PRs and accepted defaults are defined in
[the ingestion plan](docs/catalog-ingestion-plan.md); parent
[#15](https://github.com/DeandreT/weft/issues/15) completes only after both.
Each part is independently implemented and qualified; P3 planning does not
qualify persistence or login.

P3d's parent [#16](https://github.com/DeandreT/weft/issues/16) is complete after
all five focused increments qualified and merged. The exact route, identity,
cursor, metadata/head concurrency and mutation contract is in the catalog API plan.

P3e's parent [#17](https://github.com/DeandreT/weft/issues/17) completes after
the three dependent report increments qualify and merge. P3e1 is qualified and
merged in [#52](https://github.com/DeandreT/weft/pull/52). Its fresh-container
suite passed 1,635 cases, and the genuine upgrade from the previous five-migration
schema passed. The original full-run audit failed on one Unicode test display
label; a separately retained exact-row audit reconciled that display difference
without rerunning tests or changing their results. Its metadata checks use
synthetic completion facts and do not qualify native report derivation or the
report API.

P3e2 follows three independently qualified PRs in order:

1. [#53](https://github.com/DeandreT/weft/issues/53): owned report contracts,
   neutral validation, strict stored codec, and bounded response serialization.
2. [#54](https://github.com/DeandreT/weft/issues/54): trusted Ferrule report
   adapter, effective invocation limits, and verified validation links.
3. [#55](https://github.com/DeandreT/weft/issues/55): actual private-object and
   metadata orchestration, native derivation, recovery, and explicit repair.

Each successor starts after its dependencies qualify and merge. The
[stored report contract](docs/revision-report-codec.md) describes the pure first
increment; the workflow and persisted-report HTTP API retain their separate gates.

P3e2a is qualified and merged in [#56](https://github.com/DeandreT/weft/pull/56).
Its fresh-container C# suite passed 1,881 cases with no failures or skips;
actual worker and PostgreSQL checks passed, and published command-line tools
retained their runtime dependency closure. Browser and Rust evidence was inherited
for unchanged source. The first qualification build exposed a package-hash audit
error; its failed evidence remains retained separately from the corrected fresh
qualification. This increment qualifies owned contracts and the codec, rather
than report publication.

P3e2b is qualified and merged in [#57](https://github.com/DeandreT/weft/pull/57).
Its fresh-container C# suite passed 2,023 cases with no failures or skips, and
all 22 browser checks passed on an alternate display. Actual worker/database
lifecycle, resource bounds, package integrity and ordinary tool publication
checks passed; independent source and runtime reviews accepted the increment.
The worker and Ferrule pins remain unchanged. The
[trusted Ferrule report adapter](docs/ferrule-revision-reports.md) describes its
boundaries.

P3e2c is qualified and merged in [#58](https://github.com/DeandreT/weft/pull/58),
completing [#50](https://github.com/DeandreT/weft/issues/50). Its fresh-container
suite passed 2,163 cases with no failures or skips, including all 25 new actual
workflow cases. Real commit-acknowledgment loss, process death, natural lease
expiry, worker-free reopening, revocation, capacity and repair checks passed.
Independent source, runtime and packaging reviews accepted the increment; native
bytes, immutable history, dependency integrity and owned-resource cleanup remained
verified. Browser and Rust evidence retains its unchanged-input scope. The
[workflow contract](docs/revision-report-workflow.md) describes the library seam.

P3e3 is qualified and merged in [#59](https://github.com/DeandreT/weft/pull/59),
completing [#51](https://github.com/DeandreT/weft/issues/51) and
[#17](https://github.com/DeandreT/weft/issues/17). Its fresh-container full suite
passed 2,368 cases with no failures or skips, and 22 browser checks passed on an
alternate display. Actual published fresh installation and genuine historical
upgrade checks created native mappings through HTTP, derived and reloaded their
reports, and preserved them through fresh application instances and normal
same-volume database restart. Independent source, runtime and packaging reviews
accepted the increment. The
[API contract](docs/persisted-report-api.md) describes current and retained reads,
explicit derivation/repair and complete-response admission.

P3f maintenance follows four dependent, independently qualified increments:

1. [#60](https://github.com/DeandreT/weft/issues/60): retirement metadata, fenced
   cleanup claims and positive publication registration; qualified and merged in
   [#63](https://github.com/DeandreT/weft/pull/63).
2. [#61](https://github.com/DeandreT/weft/issues/61): cross-process storage fencing
   and tracked temporary publication. Its focused sequence is
   [#65](https://github.com/DeandreT/weft/issues/65), the unactivated claim-aware
   v2 filesystem component, qualified and merged in
   [#68](https://github.com/DeandreT/weft/pull/68), then
   [#66](https://github.com/DeandreT/weft/issues/66), service composition and
   explicit offline activation, qualified and merged in
   [#69](https://github.com/DeandreT/weft/pull/69), completing #61.
3. [#72](https://github.com/DeandreT/weft/issues/72): bounded read-only maintenance
   observations, the command's prerequisite; qualified and merged in
   [#73](https://github.com/DeandreT/weft/pull/73).
4. [#62](https://github.com/DeandreT/weft/issues/62): bounded run-once maintenance
   and actual database/storage/process recovery; qualified and merged in
   [#74](https://github.com/DeandreT/weft/pull/74).
   The [maintenance command contract](docs/maintenance-command.md) describes the
   explicit command, bounded evidence, continuation cursors and exit codes.

The [maintenance metadata contract](docs/private-object-maintenance-metadata.md)
keeps native history, resumable uploads and every successful derived report
protected. The metadata foundation performs no filesystem deletion. Parent
[#18](https://github.com/DeandreT/weft/issues/18) is complete after the final
command's exact-head clean regression passed 2,835 cases with no failures or
skips. Ordinary command publication, source/package stability and actual
automatic worker cleanup passed. Fresh-install and prefix-6 upgrade evidence
retains its unchanged-installation-input scope. Earlier failures remain retained.

P3g follows focused increments: read-only catalog/history, persisted mapping
explanations, project creation, metadata editing, native mapping upload/recovery,
then conditional revision append. The first increment is tracked in
[#75](https://github.com/DeandreT/weft/issues/75) and merged through
[PR #76](https://github.com/DeandreT/weft/pull/76) after 20 catalog plus 22
example/shell browser cases passed. The saved-report viewer is tracked separately
in [#77](https://github.com/DeandreT/weft/issues/77) and qualified and merged in
[PR #80](https://github.com/DeandreT/weft/pull/80), after all 24 saved-report,
20 catalog and 22 example/shell browser cases passed. Project creation and
explicit original-request recovery are tracked in
[#81](https://github.com/DeandreT/weft/issues/81). Conditional mapping metadata
editing is qualified and merged in [#85](https://github.com/DeandreT/weft/pull/85).
Its 97-case coverage comprises 55 actual editor/project/report passes inherited
with exact source and application-artifact conservation, plus 42 fresh catalog
and example/shell passes. Native mapping upload and explicit original-request
recovery are qualified and merged through [#92](https://github.com/DeandreT/weft/pull/92)
for [#84](https://github.com/DeandreT/weft/issues/84). Its 116-case coverage combines
19 inherited upload cases with exact source/product conservation and 97 fresh
metadata, project, report, catalog and example cases after the qualified
[#95](https://github.com/DeandreT/weft/pull/95) observer correction. Conditional
revision append is qualified and merged in
[PR #96](https://github.com/DeandreT/weft/pull/96), completing
[#88](https://github.com/DeandreT/weft/issues/88). Its fresh browser qualification
passed all 140 cases with no retries or skips, completing all six catalog
workflows in [#19](https://github.com/DeandreT/weft/issues/19). See
[Catalog browser](docs/catalog-browser.md), [Saved reports](docs/saved-reports.md),
[Project creation](docs/project-creation-ui.md),
[Metadata editor](docs/mapping-metadata-ui.md),
[Native mapping upload](docs/native-mapping-upload-ui.md) and
[Revision upload](docs/native-revision-append-ui.md).

The [v2 filesystem contract](docs/private-storage-v2.md) fixes the private
records, permanent gates and original-owner recovery boundary. The independent
[#64](https://github.com/DeandreT/weft/issues/64) contract correction preserves
nullable upload responses before the upload screens in #19 consume their
generated types.

P4 starts after complete qualified P3g, including revision append. Its first
focused increment, [#91](https://github.com/DeandreT/weft/issues/91), is qualified
and merged in [PR #98](https://github.com/DeandreT/weft/pull/98). It adds
provider-neutral external identity bindings, a restricted fresh reader and four
explicit owner commands. The 60 new cases passed: 34 pure, 18 actual-PostgreSQL
and 8 compiled-CLI cases. The full backend regression passed 2,895 cases with no
failures or skips, executing on the Linux host from the exact clean-container
exports. All 140 existing catalog browser cases passed across six fresh migration-8
fixtures. P4a does not activate browser authentication or grant project roles or
production-payload access. Payload permission, durable sessions, Entra callbacks
and Angular sign-in follow separate increments. See
[External identity bindings](docs/external-identity-bindings.md).

P4b [#93](https://github.com/DeandreT/weft/issues/93) adds explicit project
production-payload read grants, a restricted fresh reader and conditional owner
operations. Migration 0009 preserves revoked rows and separates this permission
from every project role. It is qualified and merged in
[PR #105](https://github.com/DeandreT/weft/pull/105): all 2,944 backend cases,
including 49 new permission cases, and all 140 browser cases passed freshly.
This foundation enables no payload endpoint or sign-in. See
[Payload permissions](docs/project-payload-permissions.md).

P4c separates the session foundation into two focused increments. Application-only
facts, pure lifetime/conditional-operation policy, and store ports in
[#97](https://github.com/DeandreT/weft/issues/97) pass locked restore, formatting
verification, Release build and all 41 new pure tests. Full discovery finds 2,985
cases; the unchanged parent has its separate 2,944-case qualification, rather than
a fresh full-suite run in this Application-only increment.
The restricted PostgreSQL foundation in
[#106](https://github.com/DeandreT/weft/issues/106) is qualified and merged in
[PR #109](https://github.com/DeandreT/weft/pull/109).
Only the completed foundation precedes protected ticket/Data Protection adapters,
cookie and epoch-bound CSRF components, then the dormant catalog identity seam.
Entra/public-route activation and Angular sign-in remain later P4d/P4e increments.
See [Server sessions](docs/server-sessions.md); pure vectors do not qualify a store
or authenticate a browser.

The protected-ticket/store-adapter module in
[#107](https://github.com/DeandreT/weft/issues/107) owns certificate-backed key
admission and minimal tickets. Cookie and epoch-CSRF components in
[#112](https://github.com/DeandreT/weft/issues/112) feed the catalog identity seam in
[#114](https://github.com/DeandreT/weft/issues/114). These contracts remain separate
from provider verification and Server activation. See
[Protected server sessions](docs/protected-server-sessions.md) and
[Browser sessions](docs/browser-server-sessions.md).

P4d has three dependent focused increments. The fixed Entra protocol adapter in
[#116](https://github.com/DeandreT/weft/issues/116) supplies request-bound verified
facts; [#118](https://github.com/DeandreT/weft/issues/118) owns the opt-in HTTP
protocol and fresh binding-to-session transition. P4d3
[#120](https://github.com/DeandreT/weft/issues/120) composes explicit disabled,
Development and Session host profiles, bounded ingress, private credential
delivery, offline TLS, diagnostic privacy and request/provider shutdown ownership.
Its acceptance uses actual published-host and restricted-database controls;
live-tenant and deployment acceptance remain separate. P4e owns Angular
session-safe presentation and sign-in controls. See
[Entra browser client](docs/entra-browser-client.md),
[Authentication HTTP](docs/browser-authentication-http.md) and
[Session host operations](docs/session-host.md).

P4e-a [#119](https://github.com/DeandreT/weft/issues/119) applies explicit status
bootstrap, identity-generation fencing and private original-operation recovery to
the Angular catalog. It preserves same-owner uncertainty without granting a new
owner access to retained work; fresh resource authority and explicit recovery
remain required. P4e-b [#122](https://github.com/DeandreT/weft/issues/122) composes
sign-in and acknowledged session controls separately ([#132](https://github.com/DeandreT/weft/pull/132)), and its owned
HTTPS browser flow covers sign-in end to end ([#164](https://github.com/DeandreT/weft/pull/164)). See
[Session-safe browser state](docs/browser-session-state.md) and
[Browser sign-in](docs/browser-signin-ui.md).

P5 follows four dependent increments: provider-neutral stream adapters and model
profiles ([#111](https://github.com/DeandreT/weft/issues/111), [#133](https://github.com/DeandreT/weft/pull/133)), private revision-pinned conversation persistence
([#124](https://github.com/DeandreT/weft/issues/124), [#134](https://github.com/DeandreT/weft/pull/134)), bounded turn orchestration ([#129](https://github.com/DeandreT/weft/issues/129), [#135](https://github.com/DeandreT/weft/pull/135)), and the
authenticated chat routes with a resumable SSE event stream and server-owned
model-profile host composition ([#136](https://github.com/DeandreT/weft/issues/136), [#153](https://github.com/DeandreT/weft/pull/153)). An opt-in check runs the
adapters against a real local model ([#147](https://github.com/DeandreT/weft/pull/147)). See [AI chat](docs/ai-chat.md),
[Chat turn orchestration](docs/chat-turn-orchestration.md) and
[Chat turn HTTP API](docs/chat-http.md).

P6 adds the five bounded read-only mapping tools ([#125](https://github.com/DeandreT/weft/issues/125), [#138](https://github.com/DeandreT/weft/pull/138)), delivery of
verified citations within chat turns ([#210](https://github.com/DeandreT/weft/issues/210), [#213](https://github.com/DeandreT/weft/pull/213)) and the Angular chat
window beside the mapping viewer ([#150](https://github.com/DeandreT/weft/issues/150), [#180](https://github.com/DeandreT/weft/pull/180)). See
[Mapping AI tools](docs/mapping-ai-tools.md) and [Chat UI](docs/chat-ui.md).

P7 starts from the Ferrule consumer pin qualified in
[#123](https://github.com/DeandreT/weft/issues/123) and follows five dependent
increments: the bounded JSON preview worker operation and adapter
([#140](https://github.com/DeandreT/weft/issues/140)), preview run persistence with
exact inputs and idempotent outputs ([#141](https://github.com/DeandreT/weft/issues/141)),
durable Wolverine execution ([#142](https://github.com/DeandreT/weft/issues/142)),
the preview HTTP API and contracts ([#143](https://github.com/DeandreT/weft/issues/143)),
and the Angular preview form ([#144](https://github.com/DeandreT/weft/issues/144)).
They are implemented in [#139](https://github.com/DeandreT/weft/pull/139), [#154](https://github.com/DeandreT/weft/pull/154), [#172](https://github.com/DeandreT/weft/pull/172), [#175](https://github.com/DeandreT/weft/pull/175), [#182](https://github.com/DeandreT/weft/pull/182) and
[#185](https://github.com/DeandreT/weft/pull/185); [#218](https://github.com/DeandreT/weft/pull/218) exercises the preview panel against the actual API and worker.
See [Ferrule worker](docs/ferrule-worker.md#json-sample-preview) and
[Preview runs](docs/preview-runs.md).

P5 can proceed after P4 while P7 proceeds in its own scoped branch. Coordinate
shared API contracts explicitly; neither branch should accumulate the other's
feature. Bound concurrent builds. Check disk space before substantial Ferrule engine-host
builds and reuse compatible artifacts.

P8a [#137](https://github.com/DeandreT/weft/issues/137) packages the server, its
operator tools and the same-replica edge as container images, with a hosted-shape
local stack ([#145](https://github.com/DeandreT/weft/pull/145), [#174](https://github.com/DeandreT/weft/pull/174)). See [Runtime packaging](docs/runtime-packaging.md).
P8b [#146](https://github.com/DeandreT/weft/issues/146) describes the first Azure
environment as Bicep templates with its operator procedure and open hosting questions
([#176](https://github.com/DeandreT/weft/pull/176)). See [Azure deployment](docs/azure-deployment.md). The Session host
composes chat, previews and revision comparison ([#211](https://github.com/DeandreT/weft/issues/211), [#214](https://github.com/DeandreT/weft/pull/214)); native upload
and report derivation reach the native worker through the hosted worker service
([#219](https://github.com/DeandreT/weft/issues/219),
[#223](https://github.com/DeandreT/weft/pull/223),
[#227](https://github.com/DeandreT/weft/pull/227)). See [Session host](docs/session-host.md).
Hosted platforms run Ferrule through the platform-agnostic, autoscaling
[Ferrule service](docs/ferrule-service.md)
([#233](https://github.com/DeandreT/weft/issues/233),
[#235](https://github.com/DeandreT/weft/pull/235),
[#237](https://github.com/DeandreT/weft/pull/237),
[#240](https://github.com/DeandreT/weft/pull/240)), with per-request isolation and
platform sandbox adapters such as Azure Container Apps dynamic sessions. Private objects
can be stored in [Azure Blob storage](docs/private-storage-blob.md)
([#232](https://github.com/DeandreT/weft/issues/232),
[#234](https://github.com/DeandreT/weft/pull/234),
[#238](https://github.com/DeandreT/weft/pull/238)); both are packaged for the hosted-shape
stack and the Azure templates in [#241](https://github.com/DeandreT/weft/pull/241).
The [hosted acceptance](docs/hosted-acceptance.md) run
([#243](https://github.com/DeandreT/weft/issues/243),
[#249](https://github.com/DeandreT/weft/pull/249),
[#250](https://github.com/DeandreT/weft/pull/250)) drives the packaged, signed-in stack
end to end with the Ferrule service and Blob storage: upload, reports, chat with verified
citations, preview, comparison and regression runs, through server restart and service
replica scaling. Its identity provider, Blob endpoint and model are local stand-ins;
an operator-approved Azure environment and Entra registration replace them.

The first hosted release is admitted only when P0-P8 are implemented and
qualified. Local-only viewer prototypes can be demonstrated earlier. Entra or
model credentials and Azure resources are not provisioned by this planning work.

## Subsequent increments

| ID  | PR scope / source ownership                                                            | Depends on  | Acceptance gate                                                                                                        | Excludes                                                   |
| --- | -------------------------------------------------------------------------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| P9  | Semantic revision comparison; domain/comparison API and UI                             | P3, P4      | Direct/conditional/control changes visible; ambiguous identity matches reported; each result cites both revisions      | Assumed native-node identity continuity                    |
| P10 | Persisted regression examples and explicit comparison policies; test/run modules       | P7          | Stored cases replay exact revisions; expected output policy is explicit; diagnostics and access/retention preserved    | Arbitrary production data capture                          |
| P11 | Immutable build/deploy contract and capability preflight; `dotnet/src/Weft.Deployment` | P7, P9, P10 | Exact runtime/format admission; build provenance; approved revision and environment references; fixture target adapter | Azure-specific rollout, arbitrary generated code execution |
| P12 | First Azure mapping endpoint target; deployment adapter and infrastructure             | P8, P11     | Restricted build worker; HTTP trigger; machine authorization; qualified artifact rollback; deployment audit evidence   | Schedules, queue triggers, broad connector support         |

P9 compares revision explanations ([#110](https://github.com/DeandreT/weft/issues/110)): the pure bounded comparison
([#151](https://github.com/DeandreT/weft/pull/151)), the retained-report HTTP read ([#152](https://github.com/DeandreT/weft/pull/152)) and the Angular comparison view
([#215](https://github.com/DeandreT/weft/pull/215)). See [Revision comparison](docs/revision-comparison.md).

P10 ([#157](https://github.com/DeandreT/weft/issues/157)) adds persisted regression cases and exact-revision runs: the
exact-output comparison policy ([#160](https://github.com/DeandreT/weft/issues/160), [#161](https://github.com/DeandreT/weft/pull/161)), immutable case envelopes
([#165](https://github.com/DeandreT/weft/issues/165), [#166](https://github.com/DeandreT/weft/pull/166)), scoped case versions ([#169](https://github.com/DeandreT/weft/issues/169), [#170](https://github.com/DeandreT/weft/pull/170)), the case HTTP API
([#173](https://github.com/DeandreT/weft/issues/173)), saved-case execution ([#178](https://github.com/DeandreT/weft/issues/178)), durable per-case runs ([#181](https://github.com/DeandreT/weft/issues/181)),
ordered schema activation ([#183](https://github.com/DeandreT/weft/issues/183), [#186](https://github.com/DeandreT/weft/pull/186)), durable dispatch ([#187](https://github.com/DeandreT/weft/issues/187), [#188](https://github.com/DeandreT/weft/pull/188)),
server composition ([#189](https://github.com/DeandreT/weft/issues/189), [#190](https://github.com/DeandreT/weft/pull/190)) and the Angular case and run views
([#191](https://github.com/DeandreT/weft/issues/191), [#192](https://github.com/DeandreT/weft/pull/192)), exercised through the actual browser and worker
([#199](https://github.com/DeandreT/weft/issues/199), [#202](https://github.com/DeandreT/weft/pull/202)). See [Regression cases](docs/regression-cases.md),
[Regression comparison](docs/regression-comparison.md),
[Regression runs](docs/regression-runs.md) and
[Regression browser](docs/regression-browser.md).

P11 ([#158](https://github.com/DeandreT/weft/issues/158)) adds immutable builds: build intent and capability preflight
([#162](https://github.com/DeandreT/weft/issues/162), [#163](https://github.com/DeandreT/weft/pull/163)), the verified artifact workflow ([#193](https://github.com/DeandreT/weft/issues/193), [#194](https://github.com/DeandreT/weft/pull/194)), approvals
and artifact persistence ([#195](https://github.com/DeandreT/weft/issues/195), [#196](https://github.com/DeandreT/weft/pull/196)), the restricted Ferrule C# compiler
worker ([#197](https://github.com/DeandreT/weft/issues/197), [#198](https://github.com/DeandreT/weft/pull/198)), durable build dispatch ([#203](https://github.com/DeandreT/weft/issues/203), [#204](https://github.com/DeandreT/weft/pull/204)), build
controls and the run API ([#205](https://github.com/DeandreT/weft/issues/205), [#206](https://github.com/DeandreT/weft/pull/206)), environment administration
([#207](https://github.com/DeandreT/weft/issues/207), [#208](https://github.com/DeandreT/weft/pull/208)), the Angular build workflow ([#209](https://github.com/DeandreT/weft/issues/209), [#212](https://github.com/DeandreT/weft/pull/212)) and restricted
compiled-runtime invocation ([#216](https://github.com/DeandreT/weft/issues/216), [#217](https://github.com/DeandreT/weft/pull/217)). See
[Build contract](docs/build-contract.md), [Build workflow](docs/build-workflow.md),
[Build HTTP](docs/build-http.md), [Build browser](docs/build-browser.md) and
[Compiled runtime](docs/compiled-runtime.md).

P12 ([#159](https://github.com/DeandreT/weft/issues/159)) targets the first Azure mapping endpoint; its first increment is
durable qualified release activation, audit and rollback ([#222](https://github.com/DeandreT/weft/issues/222),
[#225](https://github.com/DeandreT/weft/pull/225)). Azure
resources are provisioned only from an operator-approved environment.

Queue triggers, scheduled execution, additional preview formats, model providers,
identity providers, and production mapping engines each get their own scoped
increments after the relevant contracts are exercised. Full visual authoring and
AI edits require a separate proposal and revision-preservation acceptance gate.

## Verification expectations

- Keep core fixtures self-authored; do not copy Ferrule's private reference corpus.
- Contract tests cover meaningful boundaries: partial projection, permission
  denial, exact revision identity, provider translation, and worker termination.
- Test Ferrule upgrades against the adapter fixtures and admitted preview cases.
- Test AI adapters with recorded synthetic protocol fixtures in CI; live model
  checks are opt-in with configured credentials and bounded calls.
- Run browser/UI tests only on an alternate virtual display, never the user's
  active monitors. Include keyboard navigation and meaningful viewer/chat flows.
- Run .NET formatting, analyzers, builds, and tests using the pinned SDK; run
  Angular template/type checks and builds. Rust checks apply only to an optional
  Ferrule engine-host increment, with disk and build-parallelism limits.
- Verify cloud provisioning and machine credentials in a disposable configured
  environment before claiming serverless hosting or mapping deployment support.

## Open implementation choices

The first runtime PR pins toolchain and package versions. The first AI provider
and available model profiles remain a configuration/product decision. Hosting
region, tenant, cost target, payload retention, and measured execution budgets
must be settled before the corresponding hosted release gate, without blocking
the independent local contracts and viewer work.

## Planning references

- [Architecture](docs/architecture.md)
- [Catalog persistence plan](docs/catalog-persistence-plan.md)
- [Mapping backend contract](docs/mapping-backends.md)
- [AI chat architecture](docs/ai-chat.md)
