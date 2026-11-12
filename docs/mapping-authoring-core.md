# Native authoring core v1

`weft_ferrule_core::authoring::compile(&[u8])` creates a fresh Ferrule project,
applies `ferrule.edit.v1` operations and returns an authoritative `edit::Checkpoint`.
It performs no I/O and accepts no template, native project, URL or resource path.
Ferrule remains pinned to `dddc4dce6c93f4137fb2aa2a011360d089e0a51b`.

## Closed request

```json
{
  "format": "weft.authoring.v1",
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
XML metadata has required `namespace` (empty means explicitly unqualified), and
optional booleans `attribute`, `text`, `optional`, `nillable`. Omitted XML metadata
retains Ferrule's inherited-namespace behavior. XML local names are ASCII NCName
subset only. Required XML attributes and advanced XML semantics are not represented.

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
callers must inspect `view.validation.is_valid`; successful construction is not
proof of executable output, requirements coverage or user approval. Native digests
identify exact bytes, not permission to save them.

This first contract authors primary boundaries only. Named boundaries, imported
schemas, EDI constraints/custom separators, advanced cardinality, runtime profiles,
proposal persistence and approval are separate increments. Unsupported fields
are refused, never dropped. Existing engine/import capabilities are untouched.
In particular, XML/CSV/TSV authoring and mixed boundary construction **do not enable
or advertise mixed-format preview support**. Runtime admission remains separate.

## Integration overlap

New files are `core/src/authoring.rs` and `core/src/authoring/tests.rs`; the sole
shared edit is `pub mod authoring;` in `core/src/lib.rs`. No Cargo dependencies,
lockfiles, CSV/runtime modules, C#, API, UI or preview profiles are changed.

Focused check: `cargo +1.98.1 test --locked --offline -p weft-ferrule-core authoring`.
Tests cover native fidelity/determinism, all six explicit boundary identities,
real source binding, schema metadata, malformed/unknown/duplicate input,
structural limits and failed edit indexing. These are compiler tests, not format
execution qualification.

Qualification on Windows with Rust 1.98.1: `cargo +1.98.1 check --locked -p
weft-ferrule-core` passed; `cargo +1.98.1 test --locked -p weft-ferrule-core
authoring` passed all six tests. Cargo fetched the exact pinned dependencies;
manifests and lockfile were unchanged. This qualifies the native core only.
