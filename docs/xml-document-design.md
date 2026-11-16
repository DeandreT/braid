# XML document contract

## Scope

This contract defines explicit XML-to-XML execution for closed structured input
and static primary/named document outputs. Formats come from saved native
options, not upload names, stored paths, media types or XML-looking text. Ferrule
owns mapping execution.

The native/generated API and error classifier below bind to Ferrule commit
`238369bf0071ee8ea097d8c54fa5061f433807fb`.

## 1. Capability and exact admission

Use separate identifiers, following the CSV pattern:

- Capability: `braid.xml.structured-document.v1`.
- Preview/runtime profile: `braid.ferrule-xml-preview.v1`.
- Preview adapter: `braid.ferrule-xml-preview-adapter.v1`.
- C# build profile: `braid.ferrule-csharp-xml.v1`.
- Compiled execution protocol: `braid.ferrule-compiled-xml.v1`.
- Safety profile: `braid.xml-safe.v1`.
- Format/media/extension: `xml`, `application/xml`, `.xml`.

XML preview, regression and build each have an independent default-off gate.

### Exact saved FormatOptions

Admission compares Ferrule's complete decoded `FormatOptions` with an allowlisted
value and rejects duplicate or unknown raw option keys before typed normalization.
`FormatOptions` does not declare `deny_unknown_fields`, so typed equality alone
cannot detect ignored unknown keys. Known fields must be at their defaults except:

| Role | Exact non-default fields allowed |
| --- | --- |
| Primary or static named source | `xml_document = true` only |
| Primary or static named target | `xml_document = true`; optional valid `xml_schema_hints` |

Every other field must equal its default. In particular, refuse `xml_allow_inactive_root_type_members`, `xml_root_view_read_policy`, `local_xml_file_set`, `wsdl`, foreign format/transport options, and nonempty `mfd_decimal_input_names` / `mfd_optional_parser_input_names`. Those provenance maps are admitted by some current formats, but Ferrule's ordinary structured-XML lowering compares against exactly `FormatOptions { xml_document: true, ..Default::default() }`. Do not silently strip them to obtain an XML adapter.

For target hints, call `FormatOptions::validate_xml_schema_hint_options(true)` and `XmlSchemaHints::validate()`. The exact saved type is:

```text
xml_schema_hints: {
  no_namespace_location?: string,
  locations?: [{ namespace: string, location: string }]
}
```

At least one location is required; at most 32 namespace/location pairs; each token is nonempty, at most 4,096 UTF-8 bytes, and within the upstream permitted XML-character/no-XML-whitespace range; namespace strings are unique. Unknown hint members are rejected by the upstream type. These are per-value upstream maxima, not sufficient transport admission: also require the operation-wide encoded metadata and full-envelope budgets in section 4 before accepting a preview, case, or build. Preserve ordered pairs and exact lexical strings; never truncate hints to make a header fit. Locations are emitted metadata, never resources to resolve, fetch, or validate against. Input `xsi:schemaLocation` / `xsi:noNamespaceSchemaLocation` likewise must not trigger I/O.

### Schema, graph, and endpoint limits

- Require all declared boundaries, including unused/unselected declarations, to be XML and inside this capability. No mixed-format plan in v1.
- Keep braid's existing native-artifact bound of 4 MiB and conservative schema bound of depth 64 / 8,192 nodes per boundary, checked before recursive copying/lowering. Use a bounded walk, not recursive trust of arbitrary native metadata.
- Require the upstream `xml_structured_document_input_is_supported` proof for every source. This admits ordinary closed groups, repetition below the root, attributes, simple text, and supported scalar/nil forms. It does not admit arbitrary XSD, alternatives, generic/mixed content, runtime-named fields, recursive schemas, or observed RootView input.
- Narrow v1 targets to that same closed ordinary shape as well, then require the full upstream output proof. This intentionally avoids claiming all output shapes accepted by Ferrule are in braid's first XML release.
- Exactly one primary input and primary output; at most 15 static named inputs and 63 static named outputs. Names retain current uniqueness, length, and control-character checks. Root document iteration, dynamic source paths, dynamic target paths, document lists/file sets, host acquisition and network/database sources remain refused. Repetition inside a single XML document remains allowed.
- Lower once and call `prepare_boundary_emission(&program, CompanionSelection::Complete)`. Require `plan.xml()` to exist, `inputs() == XmlDocumentInputs::Structured`, `mode() == XmlOutputMode::SingleDocument`, `primary_output() == Single`, every `named_outputs()` item `Single`, and `selected_inputs().is_some()`. An ordinary successful `lower` or `emit` without this proof is not XML support: lowering can deliberately drop an optional XML adapter.
- For compiled XML, additionally qualify the exact runtime/codegen identity and the narrow projection-error classifier in section 3. An upstream typed-error change is not required to implement this pinned adapter.
- Keep parameter, clock, ownership, exact revision/digest, cancellation, and worker-isolation checks. Do not add a second evaluator or interpret the graph in braid.

## 2. XML safety and resource contract

### Why explicit preflight is required

Ferrule's general native `format_xml::from_str_with_options` uses `roxmltree::ParsingOptions { allow_dtd: true, nodes_limit: 1_000_000, ... }`. Its recursion counter is for schema/reference traversal, not a general physical-element-depth guard. That is not a safe DTD policy or proof that all physical XML input is bounded.

Ferrule also exposes the separate `format_xml::from_str_structured(text, schema)` reader. It rejects DTDs, enforces physical depth before DOM parsing, and prospectively checks materialized output before creating its `Instance`. The generated C# Structured reader has the corresponding DTD-prohibited profile. Use these facts explicitly, rather than assuming the existing native payload reader selects them.

### braid safety profile, input and output

Run these checks on original input bytes before invoking the native payload API or generated mapping, and on every output before any publication. Check the whole input/output set before publishing any part of it.

| Resource / syntax | `braid.xml-safe.v1` rule |
| --- | --- |
| Encoding | Strict UTF-8; optional initial UTF-8 BOM; declaration absent or XML 1.0 with absent/UTF-8 encoding, case-insensitive encoding spelling. Reject XML 1.1 and conflicting encodings. Never transcode. |
| Document | One well-formed root; no empty/zero-byte XML document; no fragment mode. |
| DTD | Reject every DOCTYPE, internal or external subset, including an otherwise unused declaration. Do not ignore a DTD and continue. |
| Entities | Only the five predefined XML entities and valid numeric character references. Unknown/general/parameter entities fail; no resolver, filesystem lookup, network lookup, DTD loading, or entity expansion budget that would permit a DTD. |
| Original bytes | At most 8 MiB total input and 8 MiB total output per operation, or the caller's smaller admitted limits. Consequently every document is also at most 8 MiB. Count before decoding. |
| Physical depth | Root depth 1; every start/empty element must be at most 64, including undeclared/ignored content. Check during an iterative event scan before building a DOM. |
| Physical nodes | At most 1,000,000 per document: one document node + elements + text/CDATA runs + comments + processing instructions. Adjacent text/CDATA is one run; comments/PIs break runs. Outside-root whitespace and the XML declaration are not nodes. Attributes/declarations are counted separately. |
| Attributes | At most 256 physical attributes per element, including namespace declarations. This is an additional braid limit, checked without an unbounded duplicate-attribute collector. |
| Cardinality | Existing maximum 16 inputs and 64 output artifacts; exact selected input/output identity sets; no partial result. |

Rust preflight should use the already locked `quick-xml` event reader with explicit nontrimming settings and bounded attribute enumeration. Reject `DocType` events, validate declaration/version, and count complete events rather than searching for the substring `<!DOCTYPE` (which can occur harmlessly in comments/CDATA). Then use the upstream structured reader for well-formedness, namespaces, scalar/schema admission and its additional parser/projection budgets. Add direct references only to the same pinned `ferrule-format-xml` and already locked `quick-xml` 0.41.0 during implementation. Do not copy Ferrule's schema projector into braid.

.NET preflight should use `XmlReader` with `ConformanceLevel.Document`, `DtdProcessing.Prohibit`, `XmlResolver = null`, `CheckCharacters = true`, and all of `IgnoreWhitespace`, `IgnoreComments`, `IgnoreProcessingInstructions` false. Set `MaxCharactersInDocument` to the 8 MiB safety ceiling as a secondary character bound, not a substitute for the original byte limit. Set `MaxCharactersFromEntities` explicitly to the same finite ceiling; DTD prohibition remains mandatory. Decode UTF-8 strictly first so encoding declarations cannot silently select a different byte decoder. Strip only one initial decoded BOM for the reader, not from retained bytes. Enforce event depth/node/attribute counters and reject unknown node/entity forms. Use `ReadValueChunk` or otherwise bounded values; poll cancellation between events/chunks.

### Actual upstream limits versus braid limits

The ordinary Structured native and generated readers have 64 MiB per-document input, physical depth 64, physical nodes 1,000,000, schema nodes 1,000,000, materialized nodes 1,000,000, materialized field/String bytes 64 MiB, and projection work 100,000,000. Their parser additionally bounds raw `<`/`=` reservation slots at 1,000,000, namespace references at 1,000,000, namespace-registry UTF-8 bytes at 64 MiB and structural work at 100,000,000. These are separate ledgers, not one aggregate memory guarantee. Physical names are restricted to the Basic Multilingual Plane; ordinary text may contain supplementary Unicode.

Physical XML bounds do not prove prospective projection bounds. Materializing a schema-shaped instance can charge absent fields, repeated schema work and copied field names/String values differently from physical elements or source bytes. Passing braid's byte/depth/node preflight does not make the projection limits unreachable; classify their three pinned failures explicitly before the ordinary `Input` fallback.

Generated XML input/output sets have upstream 4,096-document and 256 MiB combined-byte ceilings; generated output has a 64 MiB document ceiling. Embedded XML schema descriptors have an 8 MiB ceiling and hint descriptors 2 MiB. braid's smaller transport limits continue to win.

The general native XML writer returns a fully allocated String, and the payload API applies its own 64 MiB/document and 256 MiB/run output limits after rendering. No inspected native XML API accepts braid's smaller output ceiling as a streaming writer bound. braid's output-byte/depth/node checks therefore bound acceptance and publication, not peak allocation. Generated XML also constructs mapping results and may serialize text before byte-budget refusal. Keep the existing sandbox memory/CPU/deadline controls; do not advertise constant memory or pre-allocation rejection for output. An upstream bounded XML writer would be separate work, not a reason to build another writer/mapping engine here.

## 3. Native preview and generated execution

### Native path

Add an XML branch before the existing JSON/EDI/CSV branches. After exact mapping/endpoint admission and complete original-byte preflight, call the pinned `format_xml::from_str_structured` on primary and required named input schemas. Classify its typed limit causes as resource limits and syntax/schema/scalar failures as invalid input. Drop these validation instances before execution.

Then retain `cli::run_project_value_payloads` for the actual native mapping and serialization, with synthetic `.xml` identities, the existing host parameters/clock, and target selection. This deliberately follows the current CSV validate-then-execute pattern: the Structured reader is admission/preflight, not a replacement mapper. The payload API still uses its general XML reader, so the first pass must never be optional and no claim of identical parser routes is made. Qualification must cover any accepted-input semantic differences between that native route and the generated Structured reader. Do not enable `with_bounded_documents` globally or expect it to select `from_str_structured` for XML: the pinned XML dispatch still uses `from_str_with_options`.

Apply braid safety preflight to native serialized outputs, using each target's identity and expected XML metadata. Preserve Ferrule's returned bytes exactly. A schema-hint collision, invalid output character, later-target failure, or limit refusal returns no output set. Count each static document as one XML record; do not label repeating child elements as separate output artifacts.

### Compiler API and static XML calls

For this capability, use `ferrule_codegen::lower`, the emission proof above, then `ferrule_codegen_csharp::emit(&program)` (equivalently `emit_with_companions(..., Complete)`). The ordinary emitter adds XML APIs from the proved boundary. Do not route XML through `emit_with_static_document_adapters`: its `DocumentBoundaryOptions` enum has only Json/X12/Edifact. Do not use `emit_with_primary_xml_documents` either: it takes a `DocumentProgram` and creates an acquisition/filename-host API, which is not braid's explicit uploaded-bytes contract.

Exact generated C# surface for admitted static Structured plans:

| Operation | API / result |
| --- | --- |
| All declared outputs | `GeneratedMapping.ExecuteXmlBytesOutputsWithSources(byte[] source, IReadOnlyList<NamedXmlBytesInput> extraSources, FerruleExecutionContext executionContext)` -> `XmlBytesExecutionOutputs` |
| No named sources convenience | `ExecuteXmlBytesOutputs(byte[] source, FerruleExecutionContext executionContext)` -> same result; it supplies an empty named list |
| Selected primary or named target | `ExecuteXmlBytesSelectedTargetWithHost(byte[] source, FerruleTargetSelection selection, IReadOnlyList<NamedXmlBytesInput> extraSources, FerruleExecutionContext? executionContext = null)` -> `SelectedXmlBytesTargetOutput` |
| Selected convenience | `ExecuteXmlBytesSelectedTarget(byte[] source, FerruleTargetSelection selection)` supplies empty named inputs and no context |
| Primary convenience | `ExecuteXmlBytesWithSources(...)` / `ExecuteXmlBytes(...)` return primary bytes but execute/serialize the complete set; they are not isolated selected-target calls |

`NamedXmlBytesInput` is `(string Name, byte[] Document)`. `XmlBytesExecutionOutputs` has `byte[] Primary` and ordered `IReadOnlyList<NamedXmlBytesOutput> Extras`; each extra has `Name` and `Document`. The selected result has nested `Primary.Document` or `Named.Output` (`Name`, `Document`). These XML results do not have the generic document API's `Format`/`PrimaryFormat` properties. Bind media from the inspected XML boundary, not reflection of nonexistent format properties.

For `All`, require every static named source, including unused declarations, exactly once. For selected target calls, supply exactly the sources from Ferrule's target requirement proof, including global failure-rule dependencies. Use the generated selected API, not all-output execution followed by discarding unwanted output. The primary input remains required. The native preview uses its matching `required_sources_for_target` selection. Map declaration order to current stable braid boundary IDs, never paths supplied by the mapping.

XML methods are emitted in `GeneratedMapping.cs`; they are not a `GeneratedMapping.Xml.cs` companion. Existing runtime files remain under `Runtime/`. Keep `BuildCompilation`'s closed source rules and do not broaden them with arbitrary XML-named source files or execute the generated project file. Compilation still uses the pinned package-free .NET reference set.

### Qualified pinned projection-error classifier

In the pinned `FerruleXml.Input.Structured.cs:336-355`, `StructuredBudget.Node`, `.Bytes` and `.Work` call `StructuredInput(...)`. That helper returns a plain `FerruleXmlBoundaryException` with `Kind == Input` and no inner cause. The existing typed `FerruleXmlStructuredResourceException` covers parser budgets, not these three projection budgets. Support for this pin uses the following deliberately narrow adapter rule; a typed upstream change is not a blocker.

Identity and wrapper handling:

1. Enable this rule only for the exact XML runtime/codegen qualification identity: Ferrule commit `238369bf0071ee8ea097d8c54fa5061f433807fb`, reviewed generated runtime source, XML profile and adapter version, approved compiler provenance, and the exact artifact bound by the build intent. Resolve expected runtime `Type` objects from that verified loaded generated assembly. Require `boundary.GetType() == expectedBoundaryType` and the corresponding qualified assembly identity, not `FullName`, assembly display name, or an `is` check against an unrelated copy. Compare `Kind` to the `Input` enum value from that same qualified runtime type.
2. Unwrap the actual reflection invocation's `TargetInvocationException` once, when present. Then use only recognized XML wrappers from the same qualified assembly. `FerruleXmlExecutionException.Boundary` must be the same object as its `InnerException`; retain its `Input`, `Output` and `Request` ownership separately. For this static-input profile, `Request` is null and any input owner must match the supplied primary or declared named input. `FerruleXmlOutputSetException.Boundary` likewise equals its `InnerException`; retain its `Target`, and do not invent a lost input owner when the wrapper has none. Direct boundary failures remain unowned unless the calling route proves the owner.
3. Inspect the extracted boundary's own `Message` and `InnerException`, not an outer wrapper's message, `ToString()`, `GetBaseException()`, or an arbitrary search through the exception chain. Unexpected wrapper types, mismatched boundary/inner references, impossible ownership or wrong runtime identity do not enter this rule. They follow the existing artifact/protocol/execution failure path without being resource-classified by caption.

For a resource-limit match, all of the following must hold together: the qualified identity and actual boundary type above, `Kind == Input`, `boundary.InnerException is null`, and `StringComparison.Ordinal` equality of the entire `boundary.Message` to exactly one of:

```text
Input: XML boundary failed: XML input exceeds its materialized instance node limit
Input: XML boundary failed: XML input exceeds its materialized field/string byte limit
Input: XML boundary failed: XML input exceeds its projection work limit
```

The fixed `Input: XML boundary failed: ` prefix is part of each complete message. The three shorter captions are the constructor's `Detail` argument, not its `Message`; qualification and the rule use the full messages shown. Do not strip that prefix, trim text, fold case, accept a substring/prefix/suffix, interpolate XML values, or match `Detail` instead.

A match maps to the existing fixed public resource-limit category (`resourceLimit` in the compiled protocol and `ResourceLimit` at the application boundary), with a fixed sanitized reason and no outputs. The message does not establish an observed numeric count, so do not invent one or publish the raw caption, XML values, names, URIs, stack trace or generated text. Retain only verified ownership for bounded diagnostics.

Handle `DocumentLimit` and existing typed parser/set resource exceptions separately using their actual qualified types and structured properties. A parser exception whose outer message contains a listed caption is not a match when the boundary has an inner exception. After the specific resource cases, any other authentic qualified `Kind == Input` boundary maps to `invalidInput`; it must not be resource-classified just because its text resembles a resource error. Other kinds keep their ordinary schema/setup, mapping or output classification. A foreign identity/type/caption/kind never obtains a resource classification from this rule.

Any engine revision, runtime-source bundle, compiler/profile or relevant adapter
identity change disables this classifier and dependent compiled XML capability
until its generated-limit fixtures are checked against the new identity. The
fixtures must exercise all three failures through the real entrypoints.

Do not patch emitted runtime sources to manufacture matching failures or add a
second projection engine.

## 4. Metadata, persistence, and contract preservation

Follow CSV's resolved-metadata flow, without reusing or weakening `csvDialect`:

```text
xmlMetadata: {
  schema: "braid.xml-boundary.v1",
  safetyProfile: "braid.xml-safe.v1",
  schemaHints: null | {
    noNamespaceLocation: null | string,
    locations: [{ namespace: string, location: string }]
  }
}
```

All fields are explicit and the record is closed. Input metadata requires `schemaHints: null`; targets retain their admitted hints. The version fixes the Structured input profile and Ferrule's admitted output defaults: declaration true, indentation true, no writer-level default-namespace override. Namespace-qualified schema names remain bound by the native digest, not copied into a second schema model. No caller-editable formatting controls in v1.

- Read saved native options under the existing qualified native-source ports and exact revision/digest checks. A .NET metadata projection may extract hints/identity, but cannot establish full compiler support. Extend the CSV-style authoritative build-boundary inspection seam for XML instead of trusting projection recognition.
- Carry `xmlMetadata` on XML input/output descriptors, preview results, private saved requests, regression cases/results, build boundaries/intents, compiled request/results, and machine-endpoint documents. Reject absent, unknown-version, wrong-role, mismatching, or foreign-format XML metadata. Compare complete metadata before content comparison. Do not allow `csvDialect` and `xmlMetadata` together.
- XML-specific saved request/case/result envelopes bind metadata, safety/profile
  identity, raw-byte digest, length and media into canonical fingerprints, replay
  checks, SQL validation and read-back verification.
- XML documents must be nonempty. Reject explicit XML under a non-XML profile
  even when an XML media type is supplied.
- Keep exact-byte private storage/downloads. Browser XML display is escaped text in a bounded preview, never HTML insertion, iframe/XML navigation, XSLT, or external resource rendering. Server-selected media/download name and current attachment/CSP rules remain authoritative.
- Reuse existing source registration checks, startup-only configuration validation, lazy worker resolution and identity pinning. Reporting/ingestion remain authoritative; enabling XML must not open credentials or start workers merely to list capabilities. Existing role, ownership, request ledger, approval and authorization checks remain mandatory.

### Operation-wide encoded metadata admission

Keep the existing 128 KiB build-response header, 16 KiB direct-preview request header, 1 MiB preview-response/compiled headers and build-intent cap, 4 MiB native artifact cap, 32 MiB generated-source cap, 16-input/64-output maxima, and existing private-envelope/body caps unchanged. Do not make the new feature fit by raising a shared transport limit.

Per-field hint limits are not a safe aggregate bound. A target may carry `32 * (4096 + 4096) + 4096 = 266,240` raw UTF-8 bytes of hint tokens before JSON syntax. JSON escaping can approach six encoded bytes per source UTF-8 byte, so one legal upstream hint set can exceed the 131,072-byte build header. Across 64 outputs the independent raw maxima exceed 16 MiB. A 4 MiB native limit will reject some such cases, but still permits metadata far larger than the build header and does not bound its re-encoded size.

Use these additional XML-only rules:

1. One 32 KiB metadata ledger for the complete declared operation. Let `E(m)` count the UTF-8 JSON bytes of a complete `"xmlMetadata":<object>` property, including its name, colon, object punctuation, nested arrays/keys, explicit nulls and a reserved separator byte. Sum every primary/named input and output occurrence, including unselected and unused boundaries, before execution or source emission. Require `sum(E(m)) <= 32,768`; this is one budget shared by at most `16 + 64 = 80` boundaries, not 32 KiB per boundary or per direction. Empty-hint records consume under 128 encoded bytes each, so 80 such records consume under 10 KiB; remaining capacity is shared by every hint. Repeated hints count each time they are serialized; no name/path-based deduplication discount.
2. Count encoded bytes, not string length. Measure with the production canonical metadata JSON writer and its fixed escaping policy, including quotes, backslashes, control escapes, HTML-sensitive characters and non-ASCII/supplementary characters. Require Rust and .NET metadata budget counters to agree on shared golden fixtures. If their actual wire encoders have different escaping, reserve the larger exact encoded length for each property and enforce the full actual wire length too. Do not use raw UTF-8 length, UTF-16 length, or a serializer that builds an unbounded byte array before measuring.
3. Bounded write/count implementation. Run the actual serializer over a capped counting sink that uses checked arithmetic, fails on the first byte beyond the allowance, and retains no payload; use the same writer settings and field ordering for persisted encoding. Rust uses a capped `Write` sink with `serde_json::to_writer`; .NET uses `Utf8JsonWriter` over a capped stream. Apply the larger-encoder reservation rule through one specified common metadata encoding/counting contract, not a value trusted from the caller. After counting, write only to an equally capped destination. A byte count or digest supplied by a client never bypasses this check.
4. Full envelopes have independent bounds. Partition the XML profile's 128 KiB
   build header: at most 32 KiB metadata, 32 KiB other context/boundary JSON and
   64 KiB source-file manifest. Count every key, delimiter and separator. Before
   queuing or emission, count the boundary/context scaffold with fixed-width
   digest placeholders and maximum numeric widths, and reserve the full manifest
   allowance. After emission, check the actual manifest and complete header before
   compilation or reply. The 128-file maximum and path bounds remain independent;
   escaped names/paths may exceed the header even within item limits. A manifest
   refusal fails the job without starting a compiler or publishing partial output.
5. Do not place output metadata in the 16 KiB preview request header. That header carries only supplied input descriptors and their null-hint XML metadata; resolve the complete declared output table from the pinned native artifact inside the worker and at authorized host admission. Count the actual request, including parameters and escaped input names, against 16 KiB before queuing/execution. Up to 16 individually valid inputs/32 parameters may still exceed that shared header and must be refused. Persist any required full boundary table only in its versioned private envelope, subject to both the 32 KiB metadata ledger and that envelope's unchanged total cap.
6. Recheck every trust boundary and persisted read. Enforce the complete-declaration metadata ledger during native inspection, preview/case admission, build-intent construction and worker admission before mapping execution/source emission. Check the actual response/header/body limits before publication. For repeated serialized tables, such as intent plus replay descriptors, also count every physical occurrence against its enclosing header/body cap; the logical metadata ledger is not a discount for repeated wire bytes. Read-back recomputes the same budgets and rejects oversized/tampered records. An admission size refusal creates no new queue item, approved intent or case version; a later actual-manifest/output-envelope refusal fails the existing job with no partial successful output, retaining its normal audit history. Use a fixed resource-limit/metadata-too-large result. Never truncate hints or replace them with an unverified digest.

## 5. Regression comparison

XML regression cases compare exact original bytes, like other formats. Validate stable output keys, XML media and complete role-correct metadata/safety versions, then compare every matched output byte for byte. Report missing/unexpected output, media mismatch and XML metadata mismatch ahead of byte differences. Original uploaded/generated bytes, digests, persistence and downloads remain untouched. `BuildQualification` and `PersistedBuildIntentFactory` continue to require the existing exact-byte policy and complete all-output evidence.

## 6. Required checks

- Primary XML and primary-plus-static-named inputs/outputs; declaration-order stability; `All`, selected primary and selected named target; missing/extra/duplicate inputs; always-run failure-rule source requirements; unselected failing target stays isolated in selected mode.
- Namespaces (default, prefixed, reset, namespaced attributes), optional/repeating children, empty text/empty element, nil, escaped attributes/text, supplementary Unicode values, fixed clock and typed parameters. Target hints and collisions are verified in final bytes, with zero schema/network resolution.
- Every DTD form, internal/external/general/parameter entity attempt, unknown entities, malformed numeric references, invalid UTF-8, UTF-16 input, conflicting declarations, XML 1.1, multiple roots, malformed namespaces and duplicate attributes. Include harmless DOCTYPE-like text inside comments/CDATA to prove event-based detection.
- Physical depth 64/65 even in unknown children, node boundary/boundary-plus-one, attribute 256/257, original/aggregate byte boundary/boundary-plus-one, parser reservation/namespace-work refusals, late output failure, and cancellation. Distinguish preflight/publication limits from peak-memory observations.
- Actual generated projection-limit fixtures for materialized instance nodes, materialized field/String bytes and projection work: capture the full exception object, verify exact runtime type/assembly identity, `Kind == Input`, null boundary inner exception and one of the three complete messages, then verify braid returns only its fixed `ResourceLimit` category. Exercise primary and named input through all-output and selected-target APIs, preserving verified owner/declaration order. Include at-limit success and over-limit failure with schema/document amplification that passes physical preflight. Production generated APIs and real limit-triggering documents are required; fabricated exceptions, reflection calls to private counters, or reduced-budget source rewrites are supplemental tests only and do not qualify the runtime path.
- Classifier negative tests: foreign assembly/type with the same full name; ordinary `Exception` or mapping exception with the same text; wrong engine/runtime/compiler/profile/adapter identity; wrong `Kind`; non-null inner exception; case-only change; shortened caption; added prefix/suffix/whitespace; embedded XML value containing a caption; and a caption found only in `Detail`, an outer wrapper, `ToString()` or deeper parser cause. None is resource-classified by the compatibility rule. Authentic qualified unknown `Input` remains `invalidInput`; other kinds and foreign failures retain ordinary classification.
- Wrapper tests for the real `TargetInvocationException`, `FerruleXmlExecutionException` and `FerruleXmlOutputSetException` paths: boundary property and inner reference agree, only the boundary's own message is matched, primary/named owner is retained when present, absent owner is not invented, and inconsistent/wrong-assembly wrappers fail closed. Genuine parser failures with inner causes remain `invalidInput` unless a separately recognized typed resource cause applies. A runtime revision change disables this rule pending review, rather than silently reusing message matches. No failure publishes raw exception text or partial output.
- Metadata boundaries at exactly 32,768 encoded bytes and one over for the entire declared table; 16 inputs plus 64 outputs; many individually small hints exceeding the aggregate; a single upstream-valid hint set below the native 4 MiB limit but above the metadata cap; unselected target hints included. Cover quotes/backslashes/HTML-sensitive and supplementary Unicode escaping with shared Rust/.NET golden encoded counts. No caller-controlled counts, truncation or deduplication bypass.
- Build header partitions and actual frame tests at each boundary/one over: 32 KiB metadata, 32 KiB scaffold including escaped names, 64 KiB manifest including escaped paths, and unchanged 128 KiB total. Also test the unchanged 16 KiB preview request with 16 inputs/parameters, response/compiled/private-envelope caps, duplicate metadata occurrences in real envelopes, persisted oversized/tampered cases and prequeue/precompiler refusal. Admission failures create no new work or case; a late manifest/output failure completes the existing job as failed rather than leaving it queued or publishing a successful artifact.
- Persistence round trips/tamper refusals, exact metadata and digest mismatches,
  missing native-source composition and default-off XML rejection before
  source/worker access.
- Exact native/generated output parity for the bounded admitted cohort; `BuildQualification` and `PersistedBuildIntentFactory` continue to require original exact-byte evidence.

## Source map and references

- braid native host: `engines/ferrule-host/src/{document_format.rs,csv_document.rs,document_limits.rs,build.rs,preview/admission.rs,preview/mod.rs}`.
- braid metadata/contracts: `dotnet/src/Braid.Application/{CsvDocumentPolicy.cs,CsvNativeDialect.cs,MappingPreview.cs,Regression/RegressionComparisonService.cs,Regression/RegressionCaseVersion.cs}`; `dotnet/src/Braid.Deployment/{PersistedBuildIntentFactory.cs,BuildIntent.cs,BuildCompilation.cs,BuildQualification.cs,CompiledRuntimeProtocol.cs}`; `dotnet/apps/Braid.CompiledRuntime/Program.cs`.
- Ferrule exact saved options/hints: `crates/mapping/src/model/format_options.rs`; `crates/ir/src/xml_schema_hints.rs`.
- Ferrule parser distinctions/limits: `crates/format-xml/src/instance/{mod.rs,structured.rs,structured/parser_guards.rs}`; `crates/ir/src/xml_structured_input.rs`; `crates/cli/src/{payload.rs,payload/serialization.rs}`; `runtime/csharp/Ferrule.Runtime/FerruleXml.{Input.Structured,InputSet,OutputSet,Output}.cs`.
- Ferrule compiler and static API: `crates/codegen/src/{lower/xml_request.rs,emission_plan.rs,static_document_boundary.rs,primary_document_boundary.rs}`; `crates/codegen-csharp/src/{lib.rs,primary_xml_document.rs,mapping/xml_api.rs,mapping/xml_api/named_inputs.rs,mapping/xml_api/selected_bytes.rs}`; `docs/generated-xml-qualification-index.md`.
- Pinned classifier source: `FerruleXml.Input.Structured.cs:89-90,336-355`, `FerruleXml.InputSet.cs:13-31`, `FerruleXml.OutputSet.cs:11-18`, and the [`FerruleXml.Boundary.cs` constructor](https://github.com/DeandreT/ferrule/blob/238369bf0071ee8ea097d8c54fa5061f433807fb/runtime/csharp/Ferrule.Runtime/FerruleXml.Boundary.cs), which establishes the full-message prefix.
