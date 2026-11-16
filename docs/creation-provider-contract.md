# Creation provider contract

The workspace assistant's trusted policy is version **8**. Its instructions and
tool manifest are fingerprinted into each new turn. The tool-result envelope stays
`braid.workspace-tool-result.v1`; application plans use `braid.creation-plan.v2`.

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
controls and trailers must be declared and bound explicitly.

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
The configured requested output-token maximum is 2048. Lexical JSON tokens and
model tokens are different measures.

## Feedback integration contract

`read_specification` additionally accepts exactly:

```json
{"mode":"proposal","projectId":"<canonical UUID>","proposalId":"<canonical UUID>","fingerprint":"<64 lowercase hex characters>"}
```

This mode excludes evidence-selection keys, pagination and cursors. Its execution
is supplied by the feedback/library service, which returns
authorized bounded retained plan, assumptions/questions and source identities,
with Specification disclosure auditing, and no sample/native output.

Explicit user feedback requests a new proposal. The assistant discovers current
selections and reads fresh evidence before proposing a replacement. Each new
immutable proposal requires its own review.

## Verification

`CreationProviderContractTests` builds provider arguments directly from this wire
grammar, independently of the domain serializer. Portable tests execute actual
workspace tool parsing, generation and retention with recording ports. Native
qualification uses a synthetic provider SSE exchange and the real
`FerruleMappingCreationCompiler` adapter for all 36 format pairs, verifying native
digests, validation and retained citations. It makes no model-service calls.

Additional checks cover unsupported Position/frame/XML-root-iteration
requests, closed objects, exact/over-budget envelopes, policy fingerprints and
the mutually exclusive retained-proposal selector. A native regression verifies
absolute source paths lower to frame-pinned reads in an iterated XML child under
a singular document root. A domain-valid deeper plan is refused by the tighter
provider JSON-depth envelope. Native tests use the existing
`BRAID_AUTHORING_TEST_EXECUTABLE` portable compiler fixture.
