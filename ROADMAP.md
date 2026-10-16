# Weft delivery plan

Status: architecture, P0's local web/API shell, P1's mapping contracts, and
P2a/P2b's restricted Ferrule inspection/validation/projection backend and
P2c's Angular viewer, P3a's PostgreSQL ownership, P3b's private storage and
P3c1/P3c2's scoped native revision ingestion, P3d1's scoped catalog reads,
P3d2's conditional metadata edits, P3d3's raw native upload/recovery, and
P3d4a's explicit-capability atomic project creation persistence and P3d4b's
project creation HTTP routes are qualified and merged.
Local and independent clean-container evidence is recorded in
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
revision append is implemented in [#88](https://github.com/DeandreT/weft/issues/88);
its source, packaging and browser qualification are recorded separately.
#19 stays open until all six workflow gates pass. See
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

P5 can proceed after P4 while P7 proceeds in its own scoped branch. Coordinate
shared API contracts explicitly; neither branch should accumulate the other's
feature. Bound concurrent builds. Check disk space before substantial Ferrule engine-host
builds and reuse compatible artifacts.

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
