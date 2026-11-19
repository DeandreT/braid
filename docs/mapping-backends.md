# Mapping backend contract

## Responsibilities

braid owns discovery, ownership, revisions, presentation, authorization, and
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
| `edit` | Exact encoded checkpoint, validation and projection | Bounded typed operations replayed on a verified immutable base |
| `preview` | Ordered output artifacts, counts, bounded trace/diagnostics | Exact formats, inputs, target, limits, and host policy accepted |
| `build` | Immutable runtime artifact and provenance manifest | Exact generator/runtime capability accepted |

braid provides field-lineage and rule-detail tools over the projection. It must
not compute output values using a second implementation of mapping semantics.
Each adapter publishes the capabilities of a particular revision and operation,
not a single global boolean such as "Ferrule supports deployment."

## Choosing a document path

The machine endpoint's document request carries base64 bytes and a declared
media type for each input. It is not a generic upload endpoint. Its OpenAPI
document admits JSON, X12, EDIFACT, XML, CSV and TSV; the released build must also
admit the exact boundaries (including the saved CSV dialect). A preview that
can read a document is not evidence that the generated mapping can read it.

- **X12 and EDIFACT:** use the document preview and segment comparison. The
  compiled single-interchange profile and the explicit X12 interchange profile
  have different admission rules. Do not select the single-interchange profile
  merely because the media type matches.
- **CSV and TSV:** the saved delimiter selects the dialect, not the extension.
  Preview tables and row comparison do not rewrite downloaded bytes.
- **XML:** [structured XML previews](xml-previews.md) and saved exact-byte
  cases are separate from compiled execution. The opt-in `braid.ferrule-csharp-xml.v1`
  build requires qualified exact-byte evidence. The document endpoint requires
  `application/xml` and matching saved boundary metadata. Canonical XML
  comparison in a regression case does not qualify an artifact for release.
- **FlexText, Protobuf and XBRL:** use native previews and saved exact-byte
  cases. A layout, message descriptor or instance declaration belongs to the
  revision, not the upload. Opt-in builds admit primary string Store layouts, scalar Protobuf messages, and instant-period XBRL tables respectively. The compiled endpoint takes the media type and bytes; it reads settings from the activated release, never from caller overrides.
- **XLSX:** flat workbook read to JSON is a worker protocol, not a catalog
  preview. New workbook output needs a worksheet thread and is direct-worker
  only. There is no catalog workbook-output table or compiled endpoint.
- **PDF:** the pinned Ferrule bounded document profile rejects PDF output. A
  renderer dependency, or a worker for reading specification PDFs, is not a
  mapping-output capability. Admission requires a bounded renderer verified
  under the existing sandbox, without new privileges.
- **Database connections and updates to existing workbooks** require external
  state and are outside the pure document contract.

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
[`796eb8230561f200ca81c153c946a8fe08884e75`](https://github.com/DeandreT/ferrule/commit/796eb8230561f200ca81c153c946a8fe08884e75).
It consumes `mapping`, `ir`, and `engine` for inspection, validation and
projection, the `cli` library's in-memory payload API for bounded JSON preview,
and `codegen` and `codegen-csharp` for in-memory C# source generation in the
build operation. It never runs the Ferrule command-line program or MFD importer.
See [Ferrule worker](ferrule-worker.md#json-sample-preview) and
[Restricted Ferrule C# compiler worker](build-compiler.md).

In the engine host, pin every Ferrule Git dependency to the same full commit and commit Cargo.lock
when adding the workspace. Alias generic upstream package names (`mapping`,
`engine`, `cli`) as Ferrule-specific dependencies inside the adapter. Never use
an unpinned branch or local sibling path in CI/release manifests. Build and
qualify with the Rust toolchain that Ferrule pins at that revision, stable
`1.98.1`, and record it in the worker's `rust-toolchain.toml`, image build and
`io.braid.rust.toolchain` label.

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

Stateful SQLite and update-existing XLSX are outside the preview contract.
Inherited design-time HTTP paths do not authorize worker network access.

### Direct worker profiles for native documents

The worker accepts one primary input and produces one new primary document for
each profile below. Both boundaries must explicitly declare the formats shown. The saved
options, not filenames or media types, select Ferrule's bounded document reader
and writer. Each frame carries its byte length and SHA-256 digest, with an 8 MiB
aggregate input limit and a separate 8 MiB aggregate output limit. An execution
failure publishes no artifact frames.

| Worker protocol | Saved settings and output |
| --- | --- |
| `braid.ferrule-flextext-preview.v1` | The canonical saved FlexText layout (up to 16 KiB) must match the boundary schema. Input and output are UTF-8; a declared empty document is distinct from a missing frame. Output modes that pad fixed columns or collect whole record sets or switch arms before checking size are refused. |
| `braid.ferrule-xlsx-preview.v1` | The native codec reads a flat worksheet and writes a new workbook. Direct worker only. The restricted service refuses workbook output before execution because the pinned writer requires a worksheet thread. The catalog does not offer this profile. Updating an existing file, formulas and external links are outside this profile. |
| `braid.ferrule-xlsx-to-json-preview.v1` | Worker protocol only. Read one flat worksheet using the saved source options and return plain JSON; no workbook writer is invoked. Named boundaries, dynamic outputs and other target options are refused. |
| `braid.ferrule-protobuf-preview.v1` | Use the embedded schema, imports and root message. Return binary bytes, not text; the output must decode against the saved target schema. |
| `braid.ferrule-xbrl-preview.v1` | Read and write an instance with the saved source and target instance options. A taxonomy reference does not authorize fetching a file or making a network request. |

The catalog enables the FlexText, Protobuf and XBRL profiles separately for saved-revision previews and
exact-byte regression. Configure `BRAID_PREVIEWS_<FORMAT>_ENABLED` on the server
and preview worker, or `BRAID_REGRESSIONS_<FORMAT>_ENABLED` on the server and
regression worker, with `FORMAT` set to `FLEXTEXT`, `PROTOBUF` or `XBRL`.
The parent workflow must also be enabled. Each flag defaults to false.
Run recovery binds the stored input length, digest and canonical boundary
settings to the original revision. Regression cases retain the original bytes,
clock, limits and runtime identity. To enable bounded byte builds, set `BRAID_BUILDS_<FORMAT>_ENABLED=true` on the build API and build worker, with the same compiler identity and transport used for boundary inspection. Each flag defaults to false. Unsupported layouts remain refusals. XLSX is not included. PDF rendering and database connections
are not admitted by these profiles.

The [flat CSV document core](csv-documents.md) has a separate direct-preview and
source-emission contract. Its bounded route, dialect metadata and empty-document
semantics do not expand the existing persisted preview or deployment profiles.

Bound every native document before decoding. Ferrule's logical path acceptance is broader than braid's storage policy;
the host validates traversal-free input identities and remaps output identities
before publication. Enforce deadline/memory/CPU limits around the process because
parsing, builtin evaluation, and serialization cannot all be cooperatively
cancelled through the payload API. Treat runtime dependency enumeration as one
inspection input rather than a complete host-resource permission inventory.

If reproducible replay or diagnostic normalization needs an additional public
Ferrule API, open a focused upstream issue and PR, qualify that increment, and
pin its resulting commit in the dependent braid PR.

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

- [Ferrule project codec at the selected revision](https://github.com/DeandreT/ferrule/blob/796eb8230561f200ca81c153c946a8fe08884e75/crates/mapping/src/project_file.rs).
- [Ferrule public host exports](https://github.com/DeandreT/ferrule/blob/796eb8230561f200ca81c153c946a8fe08884e75/crates/cli/src/lib.rs).
- [Ferrule in-memory execution](https://github.com/DeandreT/ferrule/blob/796eb8230561f200ca81c153c946a8fe08884e75/crates/cli/src/payload.rs).
- [Ferrule architecture and capability boundaries](https://github.com/DeandreT/ferrule/blob/796eb8230561f200ca81c153c946a8fe08884e75/docs/architecture.md).
