# Mapping projection v1

`weft.mapping-projection.v1` is a revision-specific explanation of a mapping.
The native file remains authoritative. `Weft.Domain.NativeArtifact` owns an exact
copy of admitted bytes and computes their SHA-256; reading or projecting the
artifact cannot rewrite formatting, unknown metadata, or numeric values.

`Weft.Contracts.Projections` contains only provider-independent data. The C#
records generate `contracts/weft-v1.json` during a server build; pinned
tooling derives `apps/web/src/api/generated.ts` from that schema. Browser code
uses the generated discriminated unions rather than a second hand-written model.

## Identity and provenance

Every projection records organization, project, mapping, and immutable revision
identities, the native digest, backend identity/revision/build, adapter version,
and projection version. Node IDs are opaque strings scoped to this projection;
display names do not establish identity or imply continuity between revisions.
Native graph IDs and offsets also remain strings, preserving values beyond
JavaScript's safe integer range. Locators are evidence references, not paths that
grant file or network access.

The cache key includes the entire revision scope, native digest, backend ID,
backend revision/build, adapter build, and schema version. An adapter build must
pin its projection algorithm and relevant configuration. Changing any of these
inputs invalidates cached explanations.

## Fields and explanations

Boundaries identify primary and named inputs/outputs, format, shape, and optional
root field. A field belongs to one boundary. Paths are arrays of `field`,
`attribute`, `index`, or `anyItem` segments. A single field named `customer.name`
is distinct from two fields named `customer` and `name`. Namespace URIs stay
separate from local names; each-item selection stays separate from an index.
Unknown cardinality, requiredness, nullability, and data type remain explicit.

Rules explain output fields with value, condition, and default dependencies.
Parameters, constants, nested expression details, and native locators retain
their types. Controls describe iteration, filtering, joins, grouping, sorting,
windows, ordering, failure handling, and other behavior that a line between two
fields cannot express. Relationships derive from those canonical references;
all field, parameter, rule, control, and boundary references must resolve within
the same projection.

Integer and decimal literals are exact lexical strings. Binary floating-point
literals carry hexadecimal IEEE-754 bits, preserving negative zero and payload
bits. Absence, explicit null, and XML nil are distinct. These explanation trees
are display data and are not an executable intermediate language.

## Coverage and capability

`complete`, `partial`, and `unavailable` describe explanation coverage. A partial
view must preserve located gaps and diagnostics. An unavailable view must not
offer an apparently complete graph. A backend may return a located gap for an
unsupported construct. Exhausting its admission budget returns an explicit
resource-limit operation failure without a projection; it must not publish a
truncated graph or silently discard controls or lineage.

Operation capabilities are independent of coverage. A fully explained mapping
does not prove that preview, validation, or code generation is supported. Each
operation reports `supported`, `unsupported`, `conditional`, or `notQualified`,
with profile, boundary scope, and conditions. UI actions must check their own
operation's admission rules rather than infer support from visible fields.

## Development fixtures and versioning

With `DOTNET_ENVIRONMENT=Development`, the loopback server exposes
`GET /api/v1/fixtures/projections/{fixtureId}` for `direct`, `rich`, `partial`,
and `unavailable`, and `/openapi/v1.json`. The fixture backend is explicitly
`weft.synthetic`; these are self-authored examples, not Ferrule inspection or a
persistent catalog. Unknown fixtures and non-development environments return an
empty 404. No native artifact contents are exposed by this endpoint.

The fixtures exercise typed paths, exact values, multiple boundaries, parameters,
controls, resolvable diagnostics, and coverage separate from capability. They are
deterministic and project fresh collections so callers cannot corrupt later
reads. Serialization and Angular compile checks exercise union narrowing and
reject numeric identity/literal assignments.

Keep v1 meanings stable. New union variants or incompatible semantics require an
explicit contract review and matching browser changes; JSON deserialization
rejects unknown discriminator kinds. Add a new schema version for a breaking
change. Production upload admission and backend-specific validation belong to
their later increments.
