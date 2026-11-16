# braid delivery plan

This is the forward roadmap. GitHub issues own acceptance criteria, dependencies
and source ownership; focused documents own the technical contracts. Older plans
and verification records explain decisions, not a second release checklist.

## Delivery rules

Keep changes independently reviewable and buildable. Use conventional descriptive
branches such as `feat/<description>`. Merge verified dependencies before their
dependents; independent source work can use isolated worktrees against agreed
contracts. Before coding, record scope, exclusions, dependencies and acceptance
in the tracking issue, then link its implementation and evidence.

Keep three facts separate:

- **Implementation:** the behavior exists in source.
- **Verification:** it was checked under stated conditions. “Qualified” means verified within that scope, not universally ready.
- **Activation:** a host has the required configuration, permissions, credentials and admitted runtime dependencies.

A merge, browser fixture, documentation change or deployment template does not
establish all three. Do not remove features or weaken acceptance to shrink scope.
Server-enforced optional flags must also be reflected in UI and tool manifests.

## First release sequence

The next release outcome is [#443: create and iterate mappings from customer
specifications](https://github.com/DeandreT/braid/issues/443). The source includes
intake, typed generation, retained proposals, sample execution, feedback and
authenticated approval wiring. That is not a claim that the issue's end-to-end
real-provider acceptance has passed. Older issue assessments and branch-era
records must be read against current source and scoped evidence.

### Create, test, correct and save a new mapping

The acceptance journey must:

1. Retain customer material with source provenance: pasted text, JSON/examples,
   API documentation, Word, spreadsheets and PDF, with honest refusal of unsupported
   or unreadable content. Preserve missing requirements and conflicting assumptions.
2. Generate a **new** mapping, validate native structure and physical document
   boundaries, and cover JSON, X12, EDIFACT, XML and CSV/TSV through the admitted
   typed-plan grammar. A compiler checkpoint is not customer-guide completeness.
3. Run representative samples, show exact outputs, evidence, assumptions and open
   questions, and use explicit customer feedback to prepare a new proposal.
4. Save only the exact reviewed and approved proposal. Critical assumptions require
   acceptance; critical unanswered questions block saving. Natural-language consent,
   reconnects and model calls cannot approve. Editor changes retain a separate **Save**.
5. Verify stale-base refusal, fresh authorization, document retention, source-to-proposal
   traceability, uncertain-result recovery and same-operation retries without duplicating
   successful saves. Unrelated operations must remain independent of a failed one.

Dependencies and current contracts:

- [Specification intake](docs/spec-intake-ui.md), [document admission](docs/spec-document-intake.md)
  and [structural evidence](docs/spec-structural-evidence-ui.md): advertised capabilities,
  exact retained bytes and partial/refusal outcomes. Office/PDF require an admitted
  installed worker; the public PDF convenience factory and filesystem specification
  publication retain their refusal gates.
- [Typed creation formats](docs/mapping-creation-formats.md), [creation application](docs/mapping-creation-application.md)
  and [runtime](docs/mapping-creation-runtime.md): bounded generation, exact identities
  and durable discovery. [Server composition](docs/creation-host-composition.md) owns
  protected roles, schema checks and per-channel authoring/preview admission.
- [Proposal library](docs/creation-proposal-library.md), [review UI](docs/creation-review-ui.md)
  and [feedback contract](docs/creation-feedback-api.md): discover retained work,
  review exact proposals and explicitly send feedback.
- [#437: exact chat-edit approval](https://github.com/DeandreT/braid/issues/437),
  [proposal review](docs/chat-proposal-review-ui.md) and [mapping editing](docs/mapping-editing.md):
  proposal-specific approval/recovery, fresh head checks and private drafts. Existing
  mapping edits complement creation; they do not replace its acceptance journey.

### Remaining acceptance gates

- **Real provider:** an approved document-to-new-mapping demonstration covering the
  samples, feedback, exact approved save, retained authorization and recovery in
  [#443](https://github.com/DeandreT/braid/issues/443).
- **Azure:** authorized provisioning and deployment, protected credential delivery,
  and verification of actual hosted storage, identity, execution, restart and recovery.
  Follow [Azure deployment](docs/azure-deployment.md), [hosted acceptance](docs/hosted-acceptance.md)
  and [mapping endpoint](docs/mapping-endpoint.md); local stand-ins are not Azure acceptance.

Both gates remain user-deferred. Nothing in this roadmap authorizes provider calls,
credentials, paid resources or deployment. See the retained
[integration record](docs/v1-integration.md#remaining-acceptance-gates) for evidence
boundaries, including bounded local installed-worker and sandbox verification.
AppArmor enforcement, exhaustive hostile-input coverage and production-environment
equivalence are not established by those local results.

## Subsequent increments

Keep these as separately scoped outcomes after their prerequisite contracts and
acceptance are exercised. They are not missing-feature declarations for work
already present in source.

- **Additional execution and deployment targets:** queue triggers, schedules and
  connectors need their own authorization, side-effect/idempotency, recovery,
  retention and sandbox contracts. Build on [build workflow](docs/build-workflow.md),
  [release qualification](docs/release-qualification.md) and [release operations](docs/release-operations.md).
  The first Azure mapping endpoint outcome remains tracked by
  [#159](https://github.com/DeandreT/braid/issues/159), separately from local release activation.
- **Additional engines, model providers and identity providers:** each needs a typed
  adapter, capability admission and its own verification. Use [mapping backends](docs/mapping-backends.md),
  [AI chat](docs/ai-chat.md) and [server sessions](docs/server-sessions.md); one qualified
  adapter does not qualify another.
- **Further authoring and physical formats:** preserve the existing editor, native
  XML preview/regression path and exact-proposal edits. Additional visual authoring,
  compiled XML or format coverage needs explicit backend/runtime acceptance, not an
  assumption that interpreter support implies generator support. See
  [mapping editing #334](https://github.com/DeandreT/braid/issues/334),
  [XML previews](docs/xml-previews.md) and [compiled runtime](docs/compiled-runtime.md).
- **Organization lifecycle and automation:** reconcile proposals with the current
  migration baseline before implementation. Keep project scope, payload grants,
  audit and successful-operation retry state intact. Start from
  [project ownership](docs/project-ownership.md) and [automation](docs/automation-notifications.md).

## Verification expectations

- Use self-authored fixtures, not Ferrule's private reference corpus.
- Check partial projection, permission denial, exact revision identity, provider
  translation, bounds and real worker termination. Ferrule upgrades need adapter
  fixture and admitted-format regressions.
- Use synthetic provider protocols in CI. Real-provider checks are opt-in,
  credentialed, bounded and separately authorized.
- Keep keyboard, narrow-screen and meaningful viewer/chat flows. Browser checks
  use an isolated virtual display, never the user's active desktop.
- Use pinned toolchains, formatting/analyzers, web type/template checks and the
  applicable backend/native tests. Keep build parallelism and disk usage bounded.
- Verify durable recovery, original-operation identity and output publication;
  replay must preserve successful outcomes and not duplicate work.
- Use disposable, explicitly authorized cloud environments before claiming hosted
  storage, identity, scaling or mapping deployment acceptance.

## Open implementation choices

Toolchain pins already live in the repository manifests; this is not a future
runtime decision. Launch model profiles, permitted data destinations, tenant,
region, operational budget, retention and measured execution limits still need
the appropriate product/operator decisions before their hosted gate. They do
not block independent local source work or grant activation permission.

## Planning references

- [Documentation index](docs/README.md) and [architecture](docs/architecture.md).
- [Integration evidence](docs/v1-integration.md): historical checkpoints and their limits, not current setup instructions.
- [Catalog persistence plan](docs/catalog-persistence-plan.md): original sequencing and contract ownership.
- [Original P0–P12 delivery record](https://github.com/DeandreT/braid/blob/25e7a6c9924291081960494fa9eed707be1aa58b/ROADMAP.md): the preserved acceptance tables, exclusions, issue/PR links and historical run narratives. Current priorities are above.

Existing local design plans remain design/history references. Reconcile each with
source and owner evidence before changing its status; do not bulk-complete plans
from merges or replace unresolved requirements with a general “done”.
