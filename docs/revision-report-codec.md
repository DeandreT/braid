# Stored revision-report contract

Revision reports are derived explanations and validation findings for an
immutable native revision. The application owns accepted report snapshots.
Changing a caller's collection after acceptance cannot change a retained report,
its summary, or the bytes produced from it.

The report codec provides neutral validation, a private envelope and bounded
response serialization. See [report workflow](revision-report-workflow.md) for
derivation and [report API](persisted-report-api.md) for delivery.

## Ownership and usability

Accepted reports copy every collection and nested path, dependency, expression,
capability, diagnostic, and native owner. Getters expose read-only wrappers over
unaliased arrays. Copying and validation enforce aggregate item, depth, text, and
byte limits and observe cancellation. Unknown enum values and union variants are
refused.

An operation observation and a usable stored report are different facts. A
usable projection requires a document with matching confirmed provenance and a
successful operation. Usable validation requires an actual `true` result with
`Succeeded`, or an actual `false` result with `InvalidMapping`, and matching
confirmed provenance. Null validity never establishes a validation result.
Failures may retain actual operation observations without inventing worker
status, validity, or diagnostics.

Verified reports bind their full revision scope, native digest, backend profile,
format, policy, and reconstructed counts to immutable result metadata. They do
not establish current permission to deliver a response. The reader and
deriver must observe fresh source authority around their I/O and delivery.

Stored validation diagnostics contain native evidence with null projection
links. A later trusted linker may rebuild links after independently verifying
the exact selected projection and equal provenance. That selection changes the
response rather than the validation cache key.

## Private wire envelope

The private envelope has its own schema and codec versions. Its required fields
are `schemaVersion`, `codecVersion`, `operation`, `key`, `cacheEntryId`,
`derivationId`, `generation`, `candidateObjectId`, and `report`. The stored format
is `braid.revision-report-envelope.v1`; the codec is
`braid.revision-report-codec.v1`.

Decoding checks physically present JSON fields before constructing contract
records. A computed schema getter or an optional constructor argument cannot
prove that a required field appeared in the stored bytes. Nullable fields must
appear explicitly. Property names are ordinal and case-sensitive; duplicate
names, including escaped aliases, unknown fields, unknown discriminators,
comments, trailing commas, invalid Unicode, and a UTF-8 BOM are refused. Legal
escaped control characters remain exact string data.

Envelope identities are canonical nonzero lowercase GUID strings. Generation
is an exact positive Int64 JSON integer. The codec independently reconstructs
the complete domain key, compares its canonical bytes and fingerprints, and
checks the scoped derived-object reference against the exact owned bytes,
length, and digest. It does not accept a digest in place of source identity.

Private decoding uses its own strict JSON settings. Public HTTP serialization
settings remain unchanged. Stored-format validation resolves no current backend
or worker, so a supported retained report can be checked independently of a
live current engine.

## Neutral data and budgets

The common projection contract preserves typed field-path segments, exact
integer and decimal text, distinct null variants, and binary64 payload bits.
Binary floats use exactly 16 lowercase hexadecimal characters, including
negative zero, infinities, and NaN payloads. Ferrule-specific restrictions belong
to its adapter rather than the common contract.

Graph references must resolve inside the same document. Coverage, dependencies,
controls, and relationships must agree with their declared facts; explanation
gaps remain explicit. The metadata document-item count covers top-level document
array entries, including diagnostics and gaps. The aggregate collection budget
also counts nested entries. The response-item budget counts JSON value and
container tokens, excluding property and end tokens, with depth relative to the
standalone report.

Hard ceilings include a 16-KiB envelope header and 16-MiB stored-object and
response limits. Trusted policies may reduce them. Reads enforce both immutable
report policy and current read caps. A retained derived-only read preserves its
stored native policy because it loads no native bytes.

Trusted storage and backend registrations are lazy. Size checks can refuse an
over-limit native source or derived reference before resolving the corresponding
factory. Current effective policy includes actual adapter and derived-read caps,
so a reduced cap changes the full cache identity.

The bounded response serializer owns its returned bytes and checks cancellation
before and during writes. HTTP serialization applies it to the entire
public response wrapper, including selection and links; an inner report's size
does not establish the size of that response.
