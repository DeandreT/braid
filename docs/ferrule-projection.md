# Ferrule native projection

`IMappingProjectionBackend.ProjectAsync` produces a Weft-owned
`ProjectionDocumentV1` from an immutable native artifact. Ferrule's pinned codec
decodes the original bytes in the restricted worker. A bounded structural walker
then reads schemas, graph expressions, target bindings, scopes, failure rules,
and reusable function definitions. It does not evaluate the mapping or resolve
design-time resources. The C# adapter checks the result before returning it.

The frontend consumes the same provider-independent contract as synthetic or
future engine adapters. No Ferrule JSON parser, native graph type, or engine
execution code belongs in Angular. There is currently no native upload endpoint;
the development viewer and catalog follow in separate increments.

## Planned frontend presentation

The next Angular increment will start with an output selector and searchable output-field rows:
source fields or parameters, transformation summary, and target field. A row can
expand to show expression details, constants, conditions, defaults, and governing
controls. Multiple rules for one output field retain their binding order.

Lineage uses admitted field/rule/control relationships. Boundary lists expose
primary and named inputs and outputs. Details cover iteration, filtering, joins,
grouping, sorting, windows, ordering, failures, and function definitions/calls.
Coverage warnings will remain visible. Ambiguous source reads can retain
conservative candidate dependencies accompanied by an explanation gap; the
viewer must display their uncertainty. Ambiguous aggregate/lookup ownership
retains possible references in details without field-dependency edges.

Validation is a separate operation backed by Ferrule's validator. Its typed
native owners can navigate to a projected item only when the emitted native
origin identifies that item unambiguously. Native evidence remains available
when no unique projected item exists. Viewing an explanation does not qualify
sample execution or deployment.

## Qualified semantics

- Paths preserve field names as individual segments, including dots, slashes,
  tildes, empty names, and Unicode. Repetition uses `anyItem`; XML attributes and
  namespaces are interpreted only for an admitted XML boundary.
- Scalar item types survive repeating fields. Cardinality may have a minimum,
  maximum, both, or neither; absence of a bound is not invented as zero.
- Source reads respect document ownership, position frames, singular value
  contexts, generated positions, and nested scope selection. Ordinary grouped
  reads use the first member; uniterated repeating scalar reads use the first
  item. Those behaviors appear in details where relevant.
- Conditions and per-use runtime-parameter fallbacks retain their dependency
  roles. Preview parameter text is not promoted to a runtime default. Private
  function parameters remain part of the function explanation, not host inputs.
- Integer constants cross as exact lexical strings. Ferrule floating values
  always cross as IEEE-754 binary64 bits, including integral floats and negative
  zero. The adapter does not invent decimal precision. Absence, explicit null,
  and XML nil remain distinct.
- Dynamic or unqualified constructs produce located coverage gaps. Ambiguous
  aggregate/lookup ownership preserves possible references without claiming
  definite source dependencies. Shared graph origins retain native evidence
  without an arbitrary projected owner.

IDs and order are deterministic for the same revision and algorithm. IDs are
opaque and revision-local; display names or native IDs do not establish identity
across revisions. Locators identify retained native evidence, including the
versioned `/document` envelope and function-specific graphs.

## Admission and qualification

The worker and C# adapter bound aggregate records, paths, dependencies, details,
relationships, capabilities, diagnostics, text, depth, and response bytes. An
exact identity, path, or literal exceeding the policy fails with `ResourceLimit`
and no projection. Unsupported constructs within the budget return a visibly
partial projection. No budget failure publishes a truncated graph.

The private worker protocol rejects duplicate or unknown properties, incorrect
property casing, malformed identities, non-resolving references, noncanonical
relationships, and misleading coverage. C# constructs provenance from its
admitted artifact and trusted worker identity rather than accepting worker-supplied
public provenance. These checks do not change public HTTP JSON settings.

Self-authored native fixtures cover direct mappings, controls, functions, partial
coverage, exact values, and versioned locators. Independent direct calls to the
pinned Ferrule interpreter qualify representative scope and grouping behavior;
that interpreter is test-only and is not included as an application operation.
Live Docker tests exercise the actual Rust-to-C# projection boundary and verify
that the original artifact bytes remain unchanged. See
[Ferrule worker](ferrule-worker.md) for toolchains, image construction, and test
configuration. Azure transports, preview, and build/deploy remain unqualified.
