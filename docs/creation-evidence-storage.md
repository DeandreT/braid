# Retained mixed creation evidence

The application storage codec restores Complete byte evidence and Partial Office
or PDF evidence from exact retained canonical bytes. It uses the stored extraction
identity, including the original profile digest, rather than current extractor
defaults. Source ZIP/PDF parsing and worker execution are not part of restoration.

## Runtime integration contract

```csharp
internal static CreationEvidenceDocument CreationEvidenceDocument.RestoreRetained(
    SpecCapturedSource source,
    SpecExtractionIdentity identity,
    byte[] canonicalEvidence);
```

This is an internal trusted-store boundary available to the existing friend
assemblies. A runtime loader must supply the authoritative retained identity from
its registry and the corresponding captured source/evidence objects. It must
retain its authorization, registry identity and publication checks. The method
is not a public document-upload, worker JSON or model-output admission API.

The profile digest is an opaque retained pin: it is preserved exactly, never
recomputed or replaced. Restoration validates hard version-specific structural
ceilings, rather than claiming to reconstruct the original execution profile or
requalify its sandbox. No profile descriptor is needed for that operation. The
store's identity remains authoritative; a wholly rewritten identity and payload
are not authenticated by a self-computed digest alone. The proposal fingerprint
and exact critical coverage IDs bind that retained profile pin as before.

## Admission and grammar

- Exact source identity, format and digest must match the captured source.
- Canonical evidence must match the stored evidence digest and roundtrip exactly.
- Byte evidence requires extractor version 2, Complete, the appropriate fixed
  code and the legacy seven-field chunk grammar. Byte ranges stay within source
  bounds. Text has null JSON pointers; JSON/OpenAPI carries pointers.
- Office requires extractor version 3, Partial and the exact DOCX/XLSX coverage
  code. Chunk ordinals are contiguous; locators have the correct closed family,
  bounded coordinates and unique identities. Formula/cache/null/value-kind facts
  satisfy the same invariants as the Office process protocol.
- PDF requires extractor version 4, Partial and `pdf-page-text-only`. Every page
  remains in order, including blank pages, with null formula and the exact
  MissingText flag. MissingText must agree with whitespace-only text; at least
  one page must have usable text. The 256-page and 1 MiB text ceilings apply.
- Unknown versions, refusal outcomes, extra chunk fields, alternate whitespace or
  escaping, invalid ordinals and substituted locators fail restoration.

The existing binary envelope version 1 and block order remain unchanged. Each
source already carries its full identity and canonical evidence as separate
blocks; mixed formats need no additive envelope. Legacy encoding remains exact.
Plans still use `CopyBytes()` and `MappingCreationPlan.Parse`, preserving typed
creation-plan v2 rather than storing or parsing its compiler grammar.

Encode admits only `SupportsGeneration` evidence. Encode and Decode both run
`CreationEvidenceCoverage.Include` and require exact assumption sequence equality.
Omitted, reordered, reworded or downgraded coverage therefore fails even if a
caller supplies a matching newly computed proposal fingerprint. Citations must
identify an existing chunk of the selected exact source. Canonical re-encoding
and the final proposal fingerprint are also checked on decode.

The 64 MiB storage ceiling and independent 20 MiB aggregate captured-source and
evidence ceilings remain. Decode checks aggregates while reading blocks, before
constructing the complete specification. Individual serialized evidence ceilings
are 10 MiB for legacy byte evidence and Office, and 8 MiB for PDF. Legacy byte
evidence separately enforces the historical 2 MiB decoded budget: source byte
length plus UTF-8 JSON-pointer bytes plus 64 bytes per chunk. JSON escaping can
expand that evidence beyond 2 MiB; restoration preserves those genuine Complete
results and their exact legacy storage bytes without using current profile defaults.

## Qualification and delivery dependency

`CreationEvidenceStorageTests` uses genuine Office parser results and actual PDF
child output, with nondefault profile pins, empty PDF pages, formula cache facts,
actual native compilation of a typed v2 plan, restoration of its native bytes,
and frozen legacy storage layout. Escape-heavy genuine legacy extractions cover
400,000 quotation marks and the 10 MiB serialized boundary; ordinary text covers
the separate 2 MiB decoded boundary. Both retain old-v1 storage compatibility,
and actual extraction refuses cases just beyond either ceiling. Negative tests cover
identity/profile/digest/citation changes, rehashed malformed canonical evidence,
missing/downgraded coverage with a crafted matching fingerprint, refusal admission,
storage framing and compiler-plan substitution.

Set `WEFT_TEST_PDF_EXECUTABLE` to the reviewed native PDF worker executable and
`WEFT_AUTHORING_TEST_EXECUTABLE` to the native authoring compiler fixture, then run
the storage, generation, workflow, Office, intake and PDF process tests with the
pinned SDK. A portable PDF child test is evidence for extraction and restoration,
not Linux sandbox qualification.

The separately owned 0046 SQL/registry integration and runtime loader wiring are
still required for durable production Partial evidence. This codec supplies the
common restoration boundary for those owners.
