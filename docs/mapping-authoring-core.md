# Native authoring core v1

`braid_ferrule_core::authoring::compile(&[u8])` creates a fresh Ferrule project,
applies `ferrule.edit.v1` operations and returns an authoritative `edit::Checkpoint`.
It performs no I/O and accepts no template, native project, URL or resource path.
Ferrule remains pinned to `9149905759b4f65baf043b975624e45805a50142`.

## Closed request

```json
{
  "format": "braid.authoring.v1",
  "source": {
    "schema": {"name":"Input","shape":{"kind":"group","children":[
      {"name":"code","shape":{"kind":"scalar","scalar":"string"}}
    ]}},
    "options": {"kind":"json"}
  },
  "target": {
    "schema": {"name":"Output","shape":{"kind":"group","children":[
      {"name":"code","shape":{"kind":"scalar","scalar":"string"}}
    ]}},
    "options": {"kind":"json"}
  },
  "edit": {"format":"ferrule.edit.v1","operations":[
    {"op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"code"},
     "expression":{"kind":"source","id":"1","path":["code"]}}
  ]}
}
```

Names/children are ordered, literal identities. Scalars are `string|int|float|bool`.
Schema nodes optionally carry `repeating` (false), `nullable` (false), `required`
(empty list of declared group children) and `xml`. Nullability is JSON-only;
repeated nullable nodes are refused rather than conflating item/container nulls.
XML metadata has optional booleans `attribute`, `text`, `optional`, `nillable`.
For elements/attributes this metadata requires an explicit `namespace` string
(empty means explicitly unqualified). Text instead requires `name: "#text"`,
`xml: {"text": true}` and an absent or null `namespace`; even an empty namespace
string is refused for text. Assembly leaves text `xml_namespace` unset, as required
by pinned Ferrule's structured XML input predicate. Text must be a non-root scalar
without attribute, repetition, optionality or nillability flags. Other XML local
names retain the ASCII NCName subset. Omitted XML metadata on ordinary nodes
retains Ferrule's inherited-namespace behavior. Required XML attributes and
advanced XML semantics are not represented.

Compiler→preview fixtures exercise both accepted text namespace spellings in
JSON→XML and XML→JSON execution, simple text with a namespaced attribute, and
ordinary namespaced elements/attributes. In
`crates/ir/src/xml_structured_input.rs`, text requires
`XML_TEXT_FIELD` and `xml_namespace.is_none()`. Simple content may coexist with
attributes, but not sibling child elements in that structured runtime profile.

Options are exactly one of:

- `{"kind":"json"}`
- `{"kind":"xml"}`
- `{"kind":"x12","interchange_version":"00501"}`
- `{"kind":"edifact"}`
- `{"kind":"csv","header":true,"quote":true,"bom":false,"preserve_empty_strings":true}`
- Same CSV fields with `kind: "tsv"` (tab delimiter).

The four tabular booleans are required. Roots are groups. CSV/TSV children must be
nonempty, flat, nonrepeating scalars in wire-column order. EDI roots require an
explicit leading ISA (X12) or UNB/UNH (EDIFACT) segment group. EDI children encode
wire positions in order, including unused positions; this core does not infer
transaction layouts, fill envelopes, or certify partner-guide conformance. EDI
version/options do not create missing elements. No autocomplete is enabled.

## Admission and result

Unknown and duplicate contract fields are refused. Input is at most 1 MiB;
individual strings/keys at most 4096 UTF-8 bytes; preliminary JSON at most 100,000
values and depth 80 (serde's recursion guard also remains enabled). Combined
schemas have at most 8192 nodes and depth 24. These checks precede native assembly.
The existing edit core limits operations to 2000/1 MiB and native checkpoints to
4 MiB, validates, projects and encodes with Ferrule's faithful native codec.

`invalidAuthoring`, `authoringLimit`, `invalidSchema`, `invalidBoundary` and
`unsupportedSemantics` distinguish authoring refusals. Edit failures retain their
existing operation index. A returned checkpoint can have validation issues:
callers must inspect `view.validation.is_valid`.

This contract authors primary boundaries only. Named boundaries, imported schemas,
EDI constraints/custom separators and advanced cardinality are unsupported.
Unsupported fields are refused, never dropped. Preview checks its supported
formats separately.

## Tests

Focused check: `cargo +1.98.1 test --locked --offline -p braid-ferrule-core authoring`.
Tests cover native fidelity/determinism, all six explicit boundary identities,
real source binding, schema metadata, malformed/unknown/duplicate input,
structural limits and failed edit indexing.

## Project/session-scoped compiler transport

`braid.ferrule-authoring.v1` takes two big-endian u32-length-prefixed frames:
a closed JSON header (at most 16384 bytes), then the exact plan (at most 1 MiB),
followed by EOF. The header has `protocolVersion`, `operation: "author"`,
`requestId`, `organizationId`, `projectId`, `authoringId`, and `planDigest`.
Identities are canonical nonzero UUIDs; the digest is lowercase SHA-256 of the
exact submitted plan bytes. There is deliberately no mapping/base revision.

The reply has one bounded JSON frame (16 MiB) with the exact echoed request,
engine revision, and either `failure` or `result`. Success adds one native frame
(at most 4 MiB); result includes native digest/count, validation and projection.
A failure has no native frame. Header echo is correlation, not authorization.

The portable implementation is `core/src/authoring/transport.rs`; the production
host dispatches it from `src/lib.rs`. `FerruleInspectionBackend.AuthorAsync`
copies the input, computes its digest, verifies exact reply identity, engine,
frame lengths, native digest and report contracts, and returns a project-scoped
`MappingAuthoringCheckpoint`. It neither decodes native JSON in C# nor manufactures
a revision for report provenance. A caller must authorize project access and
retain/approve exact bytes before invoking ingestion.

The Ferrule service reserves the `authoring` profile. It serves it only with
`BRAID_FERRULE_SERVICE_AUTHORING_ENABLED=true` (default false). This uses a fresh
sandbox child with inspection-class 256 MiB/10-second resource bounds. Service
frame admission rejects authoring headers on every other profile, preventing
the disabled capability from being reached through inspection/edit routes.
Linux sandbox admission and whole-host platform requirements remain unchanged.

`core/examples/authoring_protocol.rs` is an unisolated
portable harness. Build with `cargo +1.98.1 build --locked -p braid-ferrule-core
--example authoring_protocol`; point `BRAID_AUTHORING_TEST_EXECUTABLE` at that
example when running `MappingAuthoringBackendTests`. It exercises actual Rust/C#
wire interoperability.
The core transport tests verify framing, exact identities, digest mismatch,
operation refusal and trailing-frame refusal. Whole-host/service sandbox tests
require Linux.
