# Weft delivery plan

Status: initial architecture draft. The repository foundation exists; all application increments below are planned, not implemented.

The architecture review is tracked in [#1](https://github.com/DeandreT/weft/issues/1).
The first application increment, P0, is tracked in
[#2](https://github.com/DeandreT/weft/issues/2).

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
| P0 | Buildable Rust/API and TypeScript/web shell; `crates/api`, `apps/web`, CI | Architecture review | Clean checkout builds; API health and browser shell work; Package metadata and lockfiles are consistent | Engine integration, production hosting |
| P1 | Versioned domain/API contracts and synthetic mapping projection; `crates/domain`, API schema | P0 | Generated browser types; deterministic fixture projection; no Ferrule/vendor types in public contracts | Universal execution language |
| P2 | Ferrule inspection/validation adapter and field viewer; `crates/mapping-backends`, `crates/worker`, viewer | P1 | Exact pinned codec; self-authored fixtures; lineage/controls/named outputs and partial coverage visible; inspection isolated and bounded | Preview, `.mfd`, editing |
| P3 | Catalog, ownership, immutable revision persistence; API repositories/migrations | P2 | Upload preserves artifact bytes; project-scoped queries; revisions survive restart; projection cache invalidation and blob cleanup work | Multi-organization onboarding, deployment |
| P4 | Entra session login and application permissions; identity/policy modules | P3 | Tenant/issuer/session validation; role and project access checks; CSRF and logout; separate payload grant; hosted config rejects development identity | Other providers, Azure directory administration |
| P5 | Model-provider contract, configuration, orchestration, bounded streaming; `crates/ai` | P1, P4 | Two protocol fixture adapters; one configured provider integration; authenticated stream, cancellation, usage/limit/error handling; server-side secrets | Mapping action tools, unqualified provider capabilities |
| P6 | Mapping-grounded chat window and authorized read tools; `apps/web`, AI tool registry | P2, P3, P5 | Revision-pinned answers/citations; lineage and validation tools; same interface for qualified hosted/local profiles; no mutation or payload tools | Autonomous execution/editing, vector database |
| P7 | Bounded JSON sample preview and worker lifecycle; `crates/worker`, preview UI/API | P2, P3, P4 | Outputs match upstream host API; parameters/target preflight; worker timeout/cancellation; input/output limits; no external writes or inherited network access | Stateful connectors, generated deployment |
| P8 | First Azure-hosted application environment; `deploy/azure`, runtime packaging | P4, P6, P7 | Reproducible deployment; private storage and workload identities; end-to-end viewer/chat/preview; instance restart/scale recovery | Publishing customer mappings as separate functions |

P5 can proceed after P4 while P7 proceeds in its own scoped branch. Coordinate
shared API contracts explicitly; neither branch should accumulate the other's
feature. Do not run simultaneous Rust builds or create duplicate build caches
without checking available disk space and artifact compatibility.

The first hosted release is admitted only when P0-P8 are implemented and
qualified. Local-only viewer prototypes can be demonstrated earlier. Entra or
model credentials and Azure resources are not provisioned by this planning work.

## Subsequent increments

| ID | PR scope / source ownership | Depends on | Acceptance gate | Excludes |
| --- | --- | --- | --- | --- |
| P9 | Semantic revision comparison; domain/comparison API and UI | P3, P4 | Direct/conditional/control changes visible; ambiguous identity matches reported; each result cites both revisions | Assumed native-node identity continuity |
| P10 | Persisted regression examples and explicit comparison policies; test/run modules | P7 | Stored cases replay exact revisions; expected output policy is explicit; diagnostics and access/retention preserved | Arbitrary production data capture |
| P11 | Immutable build/deploy contract and capability preflight; `crates/deployment` | P7, P9, P10 | Exact runtime/format admission; build provenance; approved revision and environment references; fixture target adapter | Azure-specific rollout, arbitrary generated code execution |
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
- Run Rust formatting, linting, unit/integration tests, and builds for changed
  crates using the pinned toolchain. Check free disk space and target-directory
  size before substantial builds; use bounded parallelism.
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
- [Mapping backend contract](docs/mapping-backends.md)
- [AI chat architecture](docs/ai-chat.md)
