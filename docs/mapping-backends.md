# Mapping backend contract

Status: proposed v1 boundary. Ferrule is the first implementation; this document
does not introduce a new executable mapping language.

## Responsibilities

Weft owns discovery, ownership, revisions, presentation, authorization, and
operation orchestration. A mapping backend owns native artifact decoding,
validation, precise transformation semantics, and runtime diagnostics.

Implement the initial adapter as ordinary Rust modules. A versioned worker
request/result envelope leaves room for remote engines later; dynamic plugins
and independent engine services are not required for the first release.

| Operation | Result | Admission |
| --- | --- | --- |
| `inspect` | Backend identity, boundaries, revision-specific capabilities, dependencies | Readable artifact under inspection limits |
| `project` | Deterministic simplified viewer document and coverage diagnostics | Supported projection schema version |
| `validate` | Structured issues with native and projected locators | Backend validation supported |
| `preview` | Ordered output artifacts, counts, bounded trace/diagnostics | Exact formats, inputs, target, limits, and host policy accepted |
| `build`, later | Immutable runtime artifact and provenance manifest | Exact generator/runtime capability accepted |

Weft provides field-lineage and rule-detail tools over the projection. It must
not compute output values using a second implementation of mapping semantics.
Each adapter publishes the capabilities of a particular revision and operation,
not a single global boolean such as "Ferrule supports deployment."

## Native artifact envelope

An immutable revision contains:

- Organization/project/mapping/revision identity.
- Backend ID and adapter build identity.
- Native document bytes and their SHA-256 digest.
- Entry document identity and a bounded manifest of any packaged dependencies.
- Native format/version metadata when the backend exposes it.
- Validation and projection status, each with its own version/provenance.

Initial admission is one Ferrule project JSON document with embedded schemas
and supported format options. `.mfd` import, arbitrary archive extraction, and
external schema/resource packages are separate increments. Inspection may show
an unresolved dependency without granting execution capability.

Native path strings are displayed as metadata. Resolving them requires an
explicit package member or approved host connection. Worker input/output
identities are traversal-free logical names; they never inherit access to the
application host's filesystem.

## Viewer projection

The projection is a read model with these components:

| Component | Contents |
| --- | --- |
| Provenance | Revision digest, backend/adapter identity, projection version |
| Boundaries | Primary/named input/output, format, root and field shapes |
| Fields | Stable revision-local ID, typed path segments, datatype, cardinality, required/nullable metadata |
| Rules | Output field, input dependencies, transform summary, constants/defaults, native locator |
| Controls | Iteration, filter, join, grouping, sorting, windows, failure conditions, ordering |
| Relationships | Typed links between fields, rules, controls, and named boundaries |
| Coverage | Complete/partial/unavailable status and unsupported-construct diagnostics |

Use typed path segments rather than splitting dot-delimited strings: native
field names can contain dots, namespaces, or other punctuation. Serialize
native numeric IDs as strings. A rule may depend on several inputs or no inputs
at all; allow constants and runtime parameters as first-class dependencies.

The default UI renders source fields, a concise transformation, and target
fields in a searchable table. Expand details to see controls and expression
structure. Display repetition, joins, filters, grouping, ordering, failure rules,
and named outputs whenever they affect meaning. A partial projection must visibly
identify gaps and retain native locators; it must not resemble a fully explained
or deployable mapping.

Projection IDs are stable within a revision. Cross-revision comparison uses
explicit semantic matching and reports ambiguity; it must not assume generated
node IDs remain constant after editing. Backend-specific extensions are namespaced
and documented, with structured fallback details for the viewer.

Backend output order and canonical field traversal determine projection ordering
so repeated inspection is reproducible. A cache key includes native artifact
digest, adapter version, and projection schema version. Different inspection
versions cannot silently share a cached explanation.

## Preview envelope

Requests identify the exact revision, selected target(s), bounded primary/named
documents, typed runtime parameters, execution purpose, and trace options.
The service supplies limits and an allowed-resource policy that the browser or
AI cannot relax. A missing required input is a structured diagnostic, not an
implicit attempt to open a design-time path.

Results identify revision/backend/runtime provenance and retain primary-then-
named output order. Each artifact includes a logical name, target identity,
media/format identity, byte count, digest, and authorized object reference.
Partial publication and engine errors are distinct statuses. Preview has no
external side effects in v1.

## Ferrule integration

Initial dependency candidate:
[`0495822e102bba3f97d9bffd3c665adfb6138bec`](https://github.com/DeandreT/ferrule/commit/0495822e102bba3f97d9bffd3c665adfb6138bec),
the upstream `main` revision inspected on 2026-10-12. This is a candidate for the
first implementation spike, not a qualified runtime release.

Pin every Ferrule Git dependency to the same full commit and commit Cargo.lock
when adding the workspace. Alias generic upstream package names (`mapping`,
`engine`, `cli`) as Ferrule-specific dependencies inside the adapter. Never use
an unpinned branch or local sibling path in CI/release manifests. Record the
compiler toolchain used to qualify that dependency; Ferrule currently documents
a nightly toolchain requirement.

Use these public boundaries at the inspected revision:

- `mapping::project_file::decode_bytes`: authoritative native file decoding.
- `engine::validate`: mapping validation.
- `cli::required_sources_for_target`: preview input preflight.
- `cli::run_project_value_payloads`: execution of an already decoded project
  from host-owned documents, with returned in-memory artifacts.
- `cli::PayloadRunOptions`: target selection, typed parameters, and tracing.

The CLI crate's public host API is reusable despite its package name. Weft should
call it as a library and keep its broad dependencies behind the adapter. If a
smaller reusable host crate becomes necessary, propose that focused change in
Ferrule instead of copying or forking its format/execution implementation.

Do not decode persisted project JSON with ordinary JavaScript number handling
or bypass Ferrule's exact file codec. Native project envelopes can preserve
finite floating-point tags and bits beyond a decimal rendering.

Start preview with self-authored JSON-to-JSON fixtures. Add XML/CSV and other
formats only through explicit capability and qualification increments. Stateful
SQLite and update-existing XLSX remain outside the initial preview contract.
Inherited design-time HTTP paths do not authorize worker network access.

Generation, `.mfd` interoperability, and browser WASM execution are optional
backend capabilities with separate acceptance gates. Do not infer any of them
from interpreter validation alone.

## Contract qualification

- An independent synthetic adapter can populate the public projection without
  importing any Ferrule types. It is a fixture adapter, not a second production engine.
- Repeated projection of one artifact is deterministic and leaves native bytes intact.
- Self-authored fixtures cover direct fields, constants, conditional/default
  rules, repetition, filters, joins, aggregation, and named outputs.
- Unknown constructs produce explicit partial coverage with native locations.
- Native/projected diagnostics resolve to the expected fields and rules.
- Preview matches Ferrule's public library result for every admitted fixture.
- Unauthorized resource paths, missing named inputs, excess budgets, worker
  timeout, and cancellation produce bounded errors with no external writes.
- Upgrading Ferrule invalidates incompatible caches and replays the contract
  fixtures before the new dependency is advertised.

## Source references

- [Ferrule project codec at the inspected revision](https://github.com/DeandreT/ferrule/blob/0495822e102bba3f97d9bffd3c665adfb6138bec/crates/mapping/src/project_file.rs).
- [Ferrule public host exports](https://github.com/DeandreT/ferrule/blob/0495822e102bba3f97d9bffd3c665adfb6138bec/crates/cli/src/lib.rs).
- [Ferrule in-memory execution](https://github.com/DeandreT/ferrule/blob/0495822e102bba3f97d9bffd3c665adfb6138bec/crates/cli/src/payload.rs).
- [Ferrule architecture and capability boundaries](https://github.com/DeandreT/ferrule/blob/0495822e102bba3f97d9bffd3c665adfb6138bec/docs/architecture.md).
