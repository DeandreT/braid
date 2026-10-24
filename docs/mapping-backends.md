# Mapping backend contract

The v1 read-model contract is implemented in
[PR #6](https://github.com/DeandreT/weft/pull/6). Ferrule's restricted native
inspection/validation adapter is qualified in
[PR #10](https://github.com/DeandreT/weft/pull/10). Native projection follows in
[#8](https://github.com/DeandreT/weft/issues/8); preview and build remain separate
capability gates. This contract does not introduce an executable mapping language.

## Responsibilities

Weft owns discovery, ownership, revisions, presentation, authorization, and
operation orchestration. A mapping backend owns native artifact decoding,
validation, precise transformation semantics, and runtime diagnostics.

Implement the application adapter in C# with a versioned worker request/result
envelope. Ferrule inspection and interpreter execution can use a restricted
engine host built from its Rust libraries; the ASP.NET Core application owns
process admission, cancellation, and result normalization. Generated .NET
mappings are an optional runtime with a separate capability gate.

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
digest and revision scope, backend revision/build, adapter build, and projection schema version. Different inspection
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

The restricted worker pins:
[`0169809123b8caebd141b26a625be4c07e750536`](https://github.com/DeandreT/ferrule/commit/0169809123b8caebd141b26a625be4c07e750536).
It consumes `mapping`, `ir`, and `engine` for inspection, validation and
projection, the `cli` library's in-memory payload API for bounded JSON preview,
and `codegen` and `codegen-csharp` for in-memory C# source generation in the
build operation. It never runs the Ferrule command-line program or MFD importer.
Native codec, validation, JSON preview, C# generation and local Linux/amd64
process isolation are qualified at that pin. Generated deployment runtimes have a
separate gate. See [Ferrule worker](ferrule-worker.md#json-sample-preview) and
[Restricted Ferrule C# compiler worker](build-compiler.md).

In the engine host, pin every Ferrule Git dependency to the same full commit and commit Cargo.lock
when adding the workspace. Alias generic upstream package names (`mapping`,
`engine`, `cli`) as Ferrule-specific dependencies inside the adapter. Never use
an unpinned branch or local sibling path in CI/release manifests. Build and
qualify with the Rust toolchain that Ferrule pins at that revision, stable
`1.98.1`, and record it in the worker's `rust-toolchain.toml`, image build and
`io.weft.rust.toolchain` label.

The worker uses these public boundaries:

- `mapping::project_file::decode_bytes`: authoritative native file decoding.
- `engine::validate`: mapping validation.
- `engine::ValidationOwner`, `ValidationEndpoint`, `ValidationSchemaStep`, and
  `ValidationScopeStep`: exact typed validation ownership.
- `engine::required_sources_for_target`: preview input preflight.
- `cli::run_project_value_payloads`: execution of an already decoded project
  from host-owned documents, with returned in-memory artifacts.
- `cli::PayloadRunOptions`: target selection, typed parameters and the
  validated `with_current_datetime` host-clock override. Tracing is not admitted,
  and the worker keeps Ferrule's default trusted execution policy because every
  payload is a host-supplied document.
- `codegen::lower` and `codegen_csharp::emit`: in-memory C# generation for the
  build operation.

The CLI crate's public host API is reusable despite its package name. The worker
calls it as a library and communicates bounded structured results to the C#
adapter; never parse CLI diagnostic text as the public protocol. If a
smaller reusable host crate becomes necessary, propose that focused change in
Ferrule instead of copying or forking its format/execution implementation.

Do not decode persisted project JSON with ordinary JavaScript number handling
or bypass Ferrule's exact file codec. Native project envelopes can preserve
finite floating-point tags and bits beyond a decimal rendering.

Start preview with self-authored JSON-to-JSON fixtures. Add XML/CSV and other
formats only through explicit capability and qualification increments. Stateful
SQLite and update-existing XLSX remain outside the initial preview contract.
Inherited design-time HTTP paths do not authorize worker network access.

Bound every native document before decoding, including legacy unmarked project
files. Ferrule's logical path acceptance is broader than Weft's storage policy;
the host validates traversal-free input identities and remaps output identities
before publication. Enforce deadline/memory/CPU limits around the process because
parsing, builtin evaluation, and serialization cannot all be cooperatively
cancelled through the payload API. Treat runtime dependency enumeration as one
inspection input rather than a complete host-resource permission inventory.

If reproducible replay or diagnostic normalization needs an additional public
Ferrule API, open a focused upstream issue and PR, qualify that increment, and
pin its resulting commit in the dependent Weft PR. Ferrule's selected revision
includes the payload-clock override merged in [#728](https://github.com/DeandreT/ferrule/pull/728).
Its presence does not activate or qualify a Weft execution profile.

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

- [Ferrule project codec at the selected revision](https://github.com/DeandreT/ferrule/blob/0169809123b8caebd141b26a625be4c07e750536/crates/mapping/src/project_file.rs).
- [Ferrule public host exports](https://github.com/DeandreT/ferrule/blob/0169809123b8caebd141b26a625be4c07e750536/crates/cli/src/lib.rs).
- [Ferrule in-memory execution](https://github.com/DeandreT/ferrule/blob/0169809123b8caebd141b26a625be4c07e750536/crates/cli/src/payload.rs).
- [Ferrule architecture and capability boundaries](https://github.com/DeandreT/ferrule/blob/0169809123b8caebd141b26a625be4c07e750536/docs/architecture.md).
