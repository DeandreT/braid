# Creation provider contract

The workspace assistant's trusted policy is version **8**. Its instructions and
tool manifest are fingerprinted into each new turn. The tool-result envelope stays
`weft.workspace-tool-result.v1`; application plans use `weft.creation-plan.v2`
with explicit legacy `weft.creation-plan.v1` compatibility.

## Provider-visible grammar

`WorkspaceToolRegistry` advertises independent JSON, XML, CSV, TSV, X12 and EDIFACT
source/target boundaries. The complete closed grammar is in the pinned
`WorkspaceChatGrounding.CreationInstructions`, referenced by the tool description
because descriptions have a 1024-byte ceiling. Names, scalar strings, option keys,
scope construction and literal syntax follow `CreationPlanV2Codec` and
`CreationPlanV2Validation`, not a provider-specific alternative format.

All schema properties are explicit, including `xml:null`, `required:[]`, and
boolean fields. Only source reads and typed constants are admitted expressions.
Source paths stay absolute in the application document; lowering supplies compiler
frame pins. Providers never supply compiler frames, operation IDs, Position
expressions or native edits. XML has one document element and no root iteration;
repeating XML children use explicit child scopes.

See [the typed format contract](mapping-creation-formats.md) for the underlying
application semantics. In particular, EDI envelopes, positional slots, composites,
controls and trailers must be declared and bound explicitly. Successful compilation
does not establish implementation-guide completeness.

## Bounded calls

The whole `propose_mapping_creation` argument object remains limited to **16384
UTF-8 bytes**, including identities, plan and notes. Provider JSON additionally
allows depth 16, 4096 lexical tokens and 2048 counted properties/array elements;
the workspace parser's depth-32 ceiling does not enlarge these limits. The domain's
separate 65536-byte application/compiler limits do not increase the provider-call
budget. Global tool/provider limits are unchanged.

Instructions require concise complete plans, or an honest refusal and a request
to narrow the mapping. They prohibit silent omission of required semantics or
splitting one mapping into independently approved fragments to evade the budget.
The tests use full synthetic X12 ISA/GS/ST/BEG/SE/GE/IEA and EDIFACT
UNB/UNH/BGM/UNT/UNZ envelopes (including composites), across all 36 boundary pairs.
These examples establish bounded reachability, not that arbitrary customer guides
fit in one call. The configured requested output-token maximum remains 2048 and
may constrain a live model sooner. Synthetic wire execution does not establish
live-model generation within that budget; lexical JSON tokens and model tokens
are different measures.

## Feedback integration contract

`read_specification` additionally accepts exactly:

```json
{"mode":"proposal","projectId":"<canonical UUID>","proposalId":"<canonical UUID>","fingerprint":"<64 lowercase hex characters>"}
```

This mode excludes evidence-selection keys, pagination and cursors. Its execution
is supplied by the separately owned feedback/library unit. That unit must return
authorized bounded retained plan, assumptions/questions and source identities,
with Specification disclosure auditing, and no sample/native output. This unit
adds only the manifest, parser and trusted instructions for that integration.

Retained context is not a fresh snapshot or approval. Only explicit user feedback
requests a new proposal. The assistant must discover current selections and read
fresh evidence before proposing a replacement; approval is independent and exact
for the new immutable proposal.

## Verification

`CreationProviderContractTests` builds provider arguments directly from this wire
grammar, independently of the domain serializer. Portable tests execute actual
workspace tool parsing, generation and retention with recording ports. Native
qualification uses a synthetic provider SSE exchange and the real
`FerruleMappingCreationCompiler` adapter for all 36 format pairs, verifying native
digests, validation and retained citations. It makes no model-service calls.

Additional checks cover legacy v1, unsupported Position/frame/XML-root-iteration
requests, closed objects, exact/over-budget envelopes, policy fingerprints and
the mutually exclusive retained-proposal selector. A native regression verifies
absolute source paths lower to frame-pinned reads in an iterated XML child under
a singular document root. A domain-valid deeper plan is refused by the tighter
provider JSON-depth envelope. Native tests use the existing
`WEFT_AUTHORING_TEST_EXECUTABLE` portable compiler fixture; this is compiler
contract qualification, not production containment admission.

Local verification: **95 passed, 0 failed, 0 skipped** across
`CreationProviderContractTests`, `CreationRuntimeToolTests`,
`WorkspaceToolServiceTests`, `WorkspaceChatOrchestrationTests` and
`WorkspaceHistoryPolicyTests`. The authoring and PDF fixture executables were
configured. Restore used only the local package cache. The installed SDK was
10.0.404 rather than the repository's 10.0.112; command-line build overrides used
`UseAppHost=false`, `RuntimeFrameworkVersion=10.0.12`,
`EnableSingleFileAnalyzer=false`, empty `RuntimeIdentifiers`, `PackAsTool=false`
and `OpenApiGenerateDocuments=false`. Repository SDK/package pins are unchanged.
