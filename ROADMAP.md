# Weft delivery plan

Status: architecture, P0's local web/API shell, P1's mapping contracts, and
P2a/P2b's restricted Ferrule inspection/validation/projection backend and
P2c's Angular viewer, P3a's PostgreSQL ownership, P3b's private storage and
P3c1's scoped ingestion metadata are
qualified and merged.
Local and independent clean-container evidence is recorded in
[PR #6](https://github.com/DeandreT/weft/pull/6) and
[PR #10](https://github.com/DeandreT/weft/pull/10) and
[PR #11](https://github.com/DeandreT/weft/pull/11) and
[PR #12](https://github.com/DeandreT/weft/pull/12) and
[PR #21](https://github.com/DeandreT/weft/pull/21) and
[PR #22](https://github.com/DeandreT/weft/pull/22) and
[PR #26](https://github.com/DeandreT/weft/pull/26).
P3c2 native ingestion is tracked in [#24](https://github.com/DeandreT/weft/issues/24).
The remaining sequence follows below.

The architecture review is tracked in [#1](https://github.com/DeandreT/weft/issues/1).
The first application increment, P0, is tracked in
[#2](https://github.com/DeandreT/weft/issues/2).
P1's mapping contracts are tracked in [#5](https://github.com/DeandreT/weft/issues/5).

## Delivery rules

Each row is one focused, independently reviewable PR. Use `feat/<description>`
branches and keep each increment buildable. Merge qualified increments before
starting dependent implementation. A feature's written contract, implementation,
and qualification are separate statuses.

Before implementation, open a scoped issue with source ownership, exclusions,
dependencies, and acceptance checks. Record the implementing PR and verification
evidence. The documentation PR does not itself qualify an application feature.

## First release sequence

| ID | PR scope / source ownership | Depends on | Acceptance gate | Excludes |
| --- | --- | --- | --- | --- |
| P0 | Buildable ASP.NET Core/API and Angular shell; `dotnet/apps/Weft.Server`, `dotnet/src/Weft.Api`, `apps/web`, CI | Architecture review | Clean checkout builds; API health and browser shell work; Package metadata and lockfiles are consistent | Engine integration, production hosting |
| P1 | Versioned domain/API contracts and synthetic mapping projection; `dotnet/src/Weft.Domain`, `dotnet/src/Weft.Contracts`, API schema | P0 | Generated browser types; deterministic fixture projection; no Ferrule/vendor types in public contracts | Universal execution language |
| P2a / [#7](https://github.com/DeandreT/weft/issues/7) | Bounded Ferrule codec/inspection/validation worker and C# client; `engines/ferrule-host`, `Weft.MappingBackends` | P1 | Pinned native codec/validator; exact framed bytes; typed owners; real isolation, timeout and cancellation checks | Projection, viewer, uploads, preview |
| P2b / [#8](https://github.com/DeandreT/weft/issues/8) | Native projection walker and adapter qualification | P2a | Deterministic fields/rules/controls/named boundaries; resolvable locators; independent expected examples and explicit gaps | Viewer, evaluation, editing |
| P2c / [#9](https://github.com/DeandreT/weft/issues/9) | Angular mapping viewer and development native examples | P2b | Search/detail/lineage/validation navigation, controls and coverage visible; keyboard and responsive browser checks | Catalog, auth, chat, preview |
| P3a / [#13](https://github.com/DeandreT/weft/issues/13) | Project ownership, application access/read contracts and scoped PostgreSQL migrations | P2c | Actual PostgreSQL roles, scope, revocation, migration concurrency, pool reuse and restart checks | Mapping/revision persistence, API, identity |
| P3b / [#14](https://github.com/DeandreT/weft/issues/14) | Private storage contract and local adapter | P3a | Create-only publication, exact bounded reads, scoped generated references and restart | Ingestion, Azure transport |
| P3c1 / [#23](https://github.com/DeandreT/weft/issues/23) | Scoped ingestion metadata, restricted writer pool and atomic publication | P3b | Actual PostgreSQL grants, ancestry, receipts, immutable facts, leases and committed order; synthetic admission evidence | Storage/native workflow, HTTP/Angular |
| P3c2 / [#24](https://github.com/DeandreT/weft/issues/24) | Exact native revision ingestion and process recovery | P3c1, P2 native adapters | Actual PostgreSQL/storage/worker; exact bytes; admission; idempotency, revocation and crash recovery | HTTP/Angular catalog, cache |
| P3d / [#16](https://github.com/DeandreT/weft/issues/16) | Scoped catalog/revision HTTP API and generated contracts | Both P3c increments | Actual API/database/storage; raw-byte upload; concurrency; explicit loopback Development identity | Entra, Angular catalog |
| P3e / [#17](https://github.com/DeandreT/weft/issues/17) | Persisted projection/validation derivations and report API | P3d, P2c contracts | Complete version/scope cache keys; guarded publication; native evidence and status preservation | Cleanup execution, viewer changes |
| P3f / [#18](https://github.com/DeandreT/weft/issues/18) | Reservation-aware orphan and derived-object maintenance | P3e | Bounded leases; publication/deletion races; crash recovery; referenced native revisions retained | Native-history purge, messaging |
| P3g / [#19](https://github.com/DeandreT/weft/issues/19) | Angular catalog, upload and immutable revision history | P3f, P2c viewer | Actual persistent API/browser flows; reopen old revision after restart; keyboard/mobile checks | Authentication, chat, preview |
| P4 | Entra session login and application permissions; identity/policy modules | P3g | Tenant/issuer/session validation; role and project access checks; CSRF and logout; separate payload grant; hosted config rejects development identity | Other providers, Azure directory administration |
| P5 | Model-provider contract, configuration, orchestration, bounded streaming; `dotnet/src/Weft.Ai` | P1, P4 | Two protocol fixture adapters; one configured provider integration; authenticated stream, cancellation, usage/limit/error handling; server-side secrets | Mapping action tools, unqualified provider capabilities |
| P6 | Mapping-grounded chat window and authorized read tools; `apps/web`, AI tool registry | P2, P3, P5 | Revision-pinned answers/citations; lineage and validation tools; same interface for qualified hosted/local profiles; no mutation or payload tools | Autonomous execution/editing, vector database |
| P7 | Bounded JSON sample preview, Wolverine durability, and worker lifecycle; `dotnet/apps/Weft.Worker`, preview UI/API | P2, P3, P4 | Outputs match upstream host API; parameters/target preflight; worker timeout/cancellation; durable job replay with idempotent output publication; input/output limits; no external writes or inherited network access | Stateful connectors, generated deployment |
| P8 | First Azure-hosted application environment; `deploy/azure`, runtime packaging | P4, P6, P7 | Reproducible deployment; private storage and workload identities; end-to-end viewer/chat/preview; instance restart/scale recovery | Publishing customer mappings as separate functions |

P3 uses the ordered ownership/storage/ingestion/API/cache/maintenance/browser
sequence in [the catalog persistence plan](docs/catalog-persistence-plan.md).
P3c's two focused PRs and accepted defaults are defined in
[the ingestion plan](docs/catalog-ingestion-plan.md); parent
[#15](https://github.com/DeandreT/weft/issues/15) completes only after both.
Each part is independently implemented and qualified; P3 planning does not
qualify persistence or login.

P5 can proceed after P4 while P7 proceeds in its own scoped branch. Coordinate
shared API contracts explicitly; neither branch should accumulate the other's
feature. Bound concurrent builds. Check disk space before substantial Ferrule engine-host
builds and reuse compatible artifacts.

The first hosted release is admitted only when P0-P8 are implemented and
qualified. Local-only viewer prototypes can be demonstrated earlier. Entra or
model credentials and Azure resources are not provisioned by this planning work.

## Subsequent increments

| ID | PR scope / source ownership | Depends on | Acceptance gate | Excludes |
| --- | --- | --- | --- | --- |
| P9 | Semantic revision comparison; domain/comparison API and UI | P3, P4 | Direct/conditional/control changes visible; ambiguous identity matches reported; each result cites both revisions | Assumed native-node identity continuity |
| P10 | Persisted regression examples and explicit comparison policies; test/run modules | P7 | Stored cases replay exact revisions; expected output policy is explicit; diagnostics and access/retention preserved | Arbitrary production data capture |
| P11 | Immutable build/deploy contract and capability preflight; `dotnet/src/Weft.Deployment` | P7, P9, P10 | Exact runtime/format admission; build provenance; approved revision and environment references; fixture target adapter | Azure-specific rollout, arbitrary generated code execution |
| P12 | First Azure mapping endpoint target; deployment adapter and infrastructure | P8, P11 | Restricted build worker; HTTP trigger; machine authorization; qualified artifact rollback; deployment audit evidence | Schedules, queue triggers, broad connector support |

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
