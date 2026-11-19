# Safe native XML previews and regression cases

`braid.ferrule-xml-preview.v1` is an explicit, default-off native preview profile.
It is distinct from JSON, JSON/EDI documents and CSV/TSV. Neither
filenames nor samples select it. Saved native options declare every XML boundary;
the full native graph remains subject to the native worker's bounded, static
document admission.

## Admission and authority

The API requires both `BRAID_PREVIEWS_ENABLED=true` and
`BRAID_PREVIEWS_XML_ENABLED=true`. The preview worker independently requires its
parent preview-worker flag and `BRAID_PREVIEWS_XML_ENABLED=true`. Invalid flag
values and missing parent workflows are configuration errors. Registration also
requires the existing native-source metadata and verified private-storage ports.
Admission flags come from trusted host configuration, not request parameters.
They do not configure regression or build workers.

The API verifies current project authority and exact saved revision identity
around private reads. Disabled XML submissions are refused before native-object
reads or queue writes. The executor refuses a stored disabled XML profile before
reading its inputs or native object. The backend preserves the flag when selecting
a per-request profile, so explicit request selection cannot bypass a disabled host.

Inputs and outputs reuse `XmlDocumentSafety`, `XmlNativeMetadata` and
`XmlDocumentMetadataJson`, described in [XML core](xml-core.md). Every declared
boundary, including unselected outputs and repeated hints, consumes the same
32 KiB encoded-metadata ledger. The existing 16-input, 64-output, native-byte,
aggregate byte, process and deadline ceilings remain unchanged. Physical input
checks precede worker dispatch. A successful result must match the saved output
boundary identities, declaration order, selected target, metadata, target-name
truncation, exact frame lengths and SHA-256 digests. Every output passes the safe
physical XML check before any complete result is returned or published.

`xmlMetadata` uses schema `braid.xml-boundary.v1` and safety profile
`braid.xml-safe.v1`. `schemaHints` is explicitly null or a closed output-only object.
Hints remain lexical output metadata. No schema, URL, local path, stylesheet or
network resource is loaded. Unknown fields, duplicate fields, null required
values and metadata from another document format are refused.

## Persistence and recovery

Canonical requests use `braid.xml-preview-request.v1`; metadata participates in
the original request fingerprint. Migration `0042_xml_documents.sql` retains XML
inputs and outputs in the separate private `xml_preview_documents` envelope.
Draft preview subjects and checkpoint execution remain separate; XML requires a
saved revision. Content, byte counts and metadata have independent
integrity checks. Existing submitter-only authorization and restricted preview
roles apply; application roles receive no direct table access.

The XML entry points use the base preview authorization checks and row-locked
completion settlement. Original-operation replay remains idempotent. Stale,
revoked, canceled or superseded attempts cannot publish outputs.
Schema admission checks the exact new function bodies, signatures, grants,
table shape, row policy, trigger, keys and index before preview operations. Current
preview pools require the XML schema even when using another profile.

XML preflight refusals return HTTP 422 with `code: "xmlPreflight"`, a closed
`kind` (`invalidInput`, `resourceLimit` or `unsupportedCapability`) and `reason`.
Optional facts are a one-based parser `location`, nonnegative physical
`statistics`, and `observed`/`limit` Int64 values encoded as exact decimal strings.
Unavailable facts are null. No parser text, exception, input name, payload value
or schema-hint URI is included. A preflight refusal creates no run or stored input.

## Browser

The browser offers XML only when `features.xmlPreview` is true. It uses the same
private draft and recovery identities as other preview submissions. Input text
is explicit XML, with named inputs and named/all/primary target selection.
Outputs provide escaped read-only raw text and a bounded lexical outline. The
outline never creates a DOM or resolves entities; documents outside its display
limits fall back to bounded raw text. Display is not schema validation and never
becomes download content. Downloads retain the full original bytes and digest.

The browser displays safe preflight reasons and numeric facts without inventing
a run or automatically retrying the mutation.

## Regression cases

XML regression cases compare exact original bytes. Publication requires
`BRAID_REGRESSIONS_ENABLED=true` and `BRAID_REGRESSIONS_XML_ENABLED=true`; the
regression worker requires its own parent worker flag and the same XML flag.
Preview and regression workers use separate backend instances, so enabling one
never admits the other's XML work. The server advertises `xmlRuntime` in the
regression capabilities only when enabled.

Cases may use named inputs and the all, primary or named target. Every input
carries input-role metadata (`schemaHints: null`) and every expected output its
admitted output metadata. Saving, replaying and evaluating a case reads the
exact native revision and requires the case's metadata to equal that revision's
complete selected declarations, including the all-target output set. The
32 KiB metadata ledger is recomputed on every read. Expected bytes pass the same
safe physical XML check as preview outputs.

Cases and results use `braid.xml-regression-case.v1` and
`braid.xml-regression-run-result.v1`, with canonical metadata and exact
re-encoding on read. Comparison reports missing or unexpected outputs, media
type and `xmlMetadataDiffer` ahead of byte differences. Equivalent XML with
different bytes is a byte difference. Stored bytes, digests and downloads are
never normalized. XML cases are not editable in the case editor: run a new
sample and explicitly save its outputs to create another baseline. Automation
comparison settlement and build qualification do not admit XML cases.

The completed preview offers baseline controls only when `xmlRuntime` is
available. Enter a test name and explicitly accept the expected output to save
the case. CSV runtime availability does not affect those XML controls.

There is no canonical comparison enablement setting. The internal canonicalizer
remains unused by regression and does not establish in-call cancellation or
allocation containment.

## Compiled execution

Compiled XML execution remains unavailable. `unsupportedXmlBuild` is the fixed
preflight refusal, even if a supplied capability snapshot claims support.

`python tools/compiled-xml/check_interfaces.py` exercises the pinned generated
byte APIs on a small authored mapping, using the locked Cargo dependencies and
pinned .NET SDK/reference pack. It compares all outputs, selected primary and
selected named output against the original native output bytes and checks the
aggregate output budget at its boundary. The driver accepts no external mapping
or assembly. This interface check does not qualify production projection limits,
the isolated runtime protocol or an HTTP endpoint.
