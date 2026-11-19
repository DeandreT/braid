# Authored-document execution foundation

`braid_ferrule_core::authored_document::execute(native, sample, limits)` executes
an unsaved native proposal entirely in memory. Both inputs are exact byte slices.
The return value contains validated output bytes, effective output dialect metadata,
the pinned engine revision, and SHA-256 digests of the native, sample and output bytes.
There is no revision identity or requirement to save the proposal first.

Implementation: `engines/ferrule-host/core/src/authored_document/`.
Ferrule revision: `796eb8230561f200ca81c153c946a8fe08884e75`.

## Executable boundaries

Execution fixtures cover every source/target pairing of **JSON, XML, CSV, TSV, X12 and EDIFACT**.
Each fixture changes an input value and
asserts that value at the correct output field after reparsing. This includes
JSON→CSV and JSON→XML. EDI fixtures contain complete interchanges, message/group
trailers and controls; element names deliberately differ from their wire positions.

The native option bags are authoritative:

- JSON: `json_document=true`, ordinary JSON only. Closed group/scalar trees,
  ordinary repetition and supported nullable flags. Root arrays use Ferrule's
  flat-row convention. Input UTF-8 BOM is accepted.
- XML: `xml_document=true`, the pinned closed structured XML reader and native
  XML writer. Exact namespaces, ordinary elements/attributes/text and supported
  optionality are retained. The structured reader rejects DTDs and unsupported
  schema shapes before projection. Output is reparsed by that reader.
- CSV/TSV: `tabular_kind=Csv`; a tab delimiter identifies TSV. A non-repeating
  flat group of scalar columns is the row schema. Delimiter, quote/quote-disabled,
  header, UTF-8 BOM and empty-string options are preserved. Native bounded readers
  and writers perform quoting and row validation. Output metadata resolves defaults.
- X12: `edi_kind=X12`; optional native separators and five-digit interchange
  version. Strict positional parsing, native document-root writing, ISA syntax
  checks and explicit envelope ownership/count/reference validation. Release
  characters are refused because the pinned complete-envelope tokenizer does not
  accept a configured release character. A declared version must match every ISA.
- EDIFACT: `edi_kind=Edifact`; native strict parsing/writing and the pinned complete
  envelope validator. Input syntax advice is interpreted by Ferrule; output uses
  its standard separators and release escaping. Metadata reports observed versions.

Success validates the emitted syntax, admitted schema projection and EDI envelopes.
It is not customer implementation-guide certification. Ferrule's native semantics
still apply, including absent scalar handling and ignored extra positional EDI
elements. Empty JSON/XML/EDI byte streams are invalid; empty CSV/TSV is zero rows.

## Executable mapping subset

The graph allowlist is **Const, SourceField and Position**, including unused nodes.
Scopes construct bindings and ordinary child groups, with either no iteration or
source iteration. At most one source-iterating scope is allowed on a root-to-leaf
path. This supports flat row mapping and structured envelope construction while
giving the host a conservative bound on constructed groups.

Other nodes (including every builtin call, UDF, runtime parameter/path, dynamic
source, lookup, aggregation and generated sequence), failure rules, named inputs
and named outputs are explicitly refused. Sorting, filtering, grouping, joins,
copy constructors and other scope controls are also refused. Recursive, open,
alternative, wildcard, fixed/default-generating and constraint-rich schemas are
outside this initial closed schema profile. An admitted XML schema must additionally
pass Ferrule's structured-input capability predicate.

Source/target paths, import-like top-level fields, HTTP/external source options and
unresolved schema/configuration references are refused. Conflicting or unrecognized
native boundary options are refused rather than guessed from a filename. JSON5,
JSON Lines, file sets, schema hints, EDI autocomplete, implied decimals, lexical
layouts and value-constraint option bags are not admitted in this unit.

The evaluator uses `ExecutionContext` with an empty lexical path because the pinned
API requires one. Path-observing nodes are refused; that value is never resolved.
No CLI acquisition, filesystem reader, schema importer or URL resolver is called.
The restricted engine policy has no roots, no origins and zero join-row authority.

## Resource contract

- Native bytes: the existing core 4 MiB decoder ceiling.
- Sample and returned output: independently lowerable limits, at most 8 MiB each.
- Schema: at most 8,192 nodes across both boundaries, depth 64, names 256 bytes,
  namespace URIs 1,024 bytes. Graph: at most 8,192 nodes.
- Engine: at most 100,000 node evaluations, active evaluation depth 64, no joins.
- Materialized input/output: at most 100,000 nodes, depth 64 and 8 MiB of counted
  names/scalars. JSON projection is prospectively bounded; EDI uses a conservative
  schema-node × segment-count reservation. CSV/XML retain their pinned bounded
  parsing policies and receive the additional materialized-instance check.
- Constructed groups: conservative source-node × scope-count reservation, capped
  at 100,000. Target writes additionally reserve scalar/name storage before insertion;
  truncated observer values reserve the largest admitted source/constant string.
- Before String-returning codecs render, a schema-shaped traversal reserves up to
  64 MiB for escaped text, names, namespaces and formatting. Fixed/default expansion
  and recursive/dynamic shapes are refused. CSV also enforces its exact byte cap
  through its native bounded sink. Every codec receives an exact final byte check.
- Elapsed budget: lowerable to a positive duration, at most five seconds. Checks
  surround stages, successful node evaluation and target writes, and follow digest
  calculation. This is **cooperative**: a running codec cannot be forcibly interrupted.
  Limits bound admitted work/data, not process RSS or a hard process wall clock.
  The process host supplies hard resource and time limits.

No bytes or metadata are returned on failure. The output is reparsed, envelopes
checked, and the complete result admitted before publication to the caller.

## Failure categories

Serialized failures are payload-free camel-case enum values:

- `invalidLimits`, `nativeLimit`, `invalidNative`
- `unsupportedBoundary`, `unsupportedSchema`, `unsupportedOperator`, `unsupportedScope`
- `namedEndpoints`, `externalDependency`, `invalidMapping`
- `inputLimit`, `invalidInput`, `executionLimit`, `executionFailed`
- `outputLimit`, `invalidOutput`, `deadlineExceeded`

Parser/engine messages, payload values, source paths and native text are not copied
into diagnostics. Reservation limits can conservatively refuse a document whose
eventual serialized output would have been smaller.

## Verification

Run from `engines/ferrule-host` on Windows with the pinned installed toolchain:

```text
cargo +1.98.1 test --locked --offline -p braid-ferrule-core
cargo +1.98.1 clippy --locked --offline -p braid-ferrule-core --all-targets -- -D warnings
```

Fixtures cover the 36-pair matrix, digest binding without saved identity, JSON/XML
escaping, namespace mismatch and DTD refusal, CSV quoting/BOM/empty strings/zero rows,
row positions, real EDI positions/envelopes/version checks/release escaping, invalid
controls, refused capabilities and input/output/work/deadline/expansion limits.

## Portable authored-preview wire boundary

`authored_document::transport::serve(reader, writer)` implements the standalone
stream; `handle(header, reader, writer)` accepts an already-framed header for
host dispatch. `core/examples/authored_preview_protocol.rs` provides the portable
stdin/stdout test process. The integrated host selects this protocol
through `src/authored_preview.rs` before the saved-profile parsers. Its handler
owns the native/sample frame limits and EOF requirement. The header dispatch cap
is the maximum of the independent caps, with the original cap enforced again
before the saved-profile parser; both caps are currently 16 KiB.

Build it with:

```text
cargo +1.98.1 build --locked --offline -p braid-ferrule-core --example authored_preview_protocol
```

Protocol: `braid.ferrule-authored-preview.v1`; operation: `previewAuthored`.
The request has exactly these fields:

```text
protocolVersion, requestId, operation, organizationId, projectId, authoringId,
proposalDigest, nativeDigest, sampleDigest
```

UUIDs are canonical lowercase nonzero GUIDs; digests are 64 lowercase hexadecimal
characters. Identity is an exact correlation echo, not authorization. Native/sample
digests are checked against the captured frame bytes. `proposalDigest` is validated
and echoed, but cannot be independently verified because proposal bytes are not in
this protocol; the project owner must preserve that proposal/native association.

Request: three big-endian u32 length-prefixed frames: JSON header ≤16 KiB,
nonempty native ≤4 MiB, sample ≤1 MiB, then exactly EOF. A zero-length sample is
valid framing (CSV/TSV can execute it); format parsing decides its meaning.

Response header:

```json
{
  "protocolVersion": "braid.ferrule-authored-preview.v1",
  "request": "exact parsed request object, or null if unavailable",
  "engineRevision": "796eb8230561f200ca81c153c946a8fe08884e75",
  "result": "null or {outputDigest, outputByteCount, format, mediaType}",
  "failure": "null or {code}"
}
```

Exactly one of result/failure is non-null. A success has a second output frame
≤1 MiB, including a zero-byte output frame where appropriate. A failure has only
the header frame. The reply header is independently bounded to 16 KiB. If an
invalid request's echo would overflow that bound, the fallback is `resourceLimit`
with `request=null`. Malformed headers similarly have no request echo.

Closed format/media pairs are `json`/`application/json`, `xml`/`application/xml`,
`csv`/`text/csv`, `tsv`/`text/tab-separated-values`, `x12`/`application/edi-x12`,
and `edifact`/`application/edifact`. `Format::id()` and `Format::media_type()` expose
the same closed values. Core dialect metadata remains available in `Output`;
the fixed v1 wire header intentionally carries only the agreed format/media pair.

The closed wire failure codes and their core mappings are:

- `invalidFrame`: bad/truncated/oversized frame, missing frame or trailing bytes.
- `invalidHeader`: invalid JSON, duplicate/unknown/missing fields or header shape.
- `unsupportedOperation`: protocol or operation mismatch.
- `invalidContext`: invalid identity or digest spelling.
- `digestMismatch`: native or sample byte digest mismatch.
- `invalidMapping`: `InvalidNative`, `InvalidMapping`.
- `unsupportedSemantics`: all four `Unsupported*` core categories,
  `NamedEndpoints`, `ExternalDependency`.
- `invalidSample`: `InvalidInput`.
- `executionFailed`: `ExecutionFailed`, `InvalidOutput`.
- `resourceLimit`: all core limit categories, including `DeadlineExceeded`.

Failures carry only `code`, never an upstream error string. The process host must
enforce a deadline on stdin EOF and blocked I/O.
