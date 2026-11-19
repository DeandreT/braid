# Typed mapping creation plan v2

`MappingCreationPlan(CreationPlanV2)` extends the application authoring boundary to
JSON, XML, CSV, TSV, X12 and EDIFACT, independently on either side. It lowers a
closed typed plan to `braid.authoring.v1` plus `ferrule.edit.v1` operations. The
Ferrule dependency stays pinned to `9149905759b4f65baf043b975624e45805a50142`.

## Storage

Store the canonical `CopyBytes()` and restore with `MappingCreationPlan.Parse`.
The strict version-dispatch parser rebuilds the typed model and computes both
digests. Do not use reflection-based serialization of the public model or substitute application bytes for
`CopyCompilerPlanBytes()`. The existing 65,536-byte ceiling applies independently
to application and compiler documents, including v2. The compiler adapter uses
the copy/digest surface.

## Closed application document

The root has exactly `schema: "braid.creation-plan.v2"`, `source`, `target`, and
`scope`. A boundary has exactly `schema` and `options`. All canonical schema
properties are explicit:

```json
{
  "name": "Output",
  "shape": {"kind": "group", "children": []},
  "repeating": false,
  "nullable": false,
  "required": [],
  "xml": null
}
```

A scalar shape is `{"kind":"scalar","scalar":"string|int|float|bool"}` (one
literal scalar name). Group children are ordered and have unique names. Required
members name declared direct children. JSON supports scalar and group nullability;
repeated nullable schemas are refused because item/container nullability would
otherwise be ambiguous. A binding can emit an explicit JSON null to a nullable
scalar. Group constructors construct ordinary groups; they do not synthesize a
null group. Root schemas are groups. Repeated scalar bindings are outside the
compiler's scalar-binding contract.

Options are genuine native option bags, with every tabular boolean mandatory:

```json
{"kind":"json"}
{"kind":"xml"}
{"kind":"csv","header":true,"quote":true,"bom":false,"preserve_empty_strings":true}
{"kind":"tsv","header":true,"quote":true,"bom":false,"preserve_empty_strings":true}
{"kind":"x12","interchange_version":"00401"}
{"kind":"edifact"}
```

CSV/TSV roots are singular flat nonempty ordered scalar-column groups. Header,
quoting, UTF-8 BOM and empty-string behavior are preserved. TSV selects a tab
delimiter. X12 requires an explicit five-digit interchange version. EDI roots
begin with an ISA group or an UNB/UNH group, respectively. Envelope segments,
composites, controls, unused positional slots and trailers must be explicitly
declared and bound; element names are identities, while order determines wire
positions. Explicit constants can fill known envelope values. Native execution
checks envelope counts and references on actual output.

## XML roles

An absent `xml` value inherits native namespace behavior. A metadata object has
exactly `namespace`, `attribute`, `text`, `optional`, and `nillable`. An empty
namespace string is explicitly unqualified; a nonempty string is an exact URI.
Ordinary element/attribute metadata requires a string namespace. Local names use
the compiler's ASCII NCName subset.

Text uses the reserved `#text` schema name with `text:true` and canonical
`namespace:null`. It has no XML name/namespace of its own. It must be a singular
scalar and may coexist only with attributes (simple content), not child elements.
Optional singular elements are supported with `optional:true`; optional repeating
nodes, optional roots, optional text/attribute role flags, namespace-declaration
attributes, reserved xsi:nil/xsi:type attributes, mixed content, and nillable
schemas are refused. Pinned preview does not admit nillable metadata even though
the compiler alone accepts it. Attribute absence follows the pinned ordinary
attribute semantics. Required-attribute constraints are outside the authoring
grammar. Namespace URIs are bounded and reject whitespace and reserved/conflicting
identities.

## Bindings and scopes

A scope has exactly `target`, `iteration`, `bindings`, and `children`. The root
target is null; a child scope names a declared target group. Iteration is null
for a single constructor or an array of absolute source path components. An empty
array selects source rows; a nonempty array selects a declared repeating source
group. At most one source-iterating scope is allowed on each root-to-leaf path.
Child source iteration requires a repeating target group. Ordinary nested scopes
can construct EDI envelopes and composites around the mapped values.
XML roots cannot iterate, including an empty-path identity iteration: the native
evaluator produces a repeated root and the XML writer requires one document
element. Iterate a repeating child below that document root instead.

Bindings name a unique declared target scalar and a closed value:

```json
{"target":"code","value":{"kind":"source","path":["rows","code"]}}
{"target":"code","value":{"kind":"constant","value":{"type":"string","text":"ABC"}}}
{"target":"count","value":{"kind":"constant","value":{"type":"int","text":"3"}}}
{"target":"empty","value":{"kind":"constant","value":{"type":"json_null"}}}
```

Source paths stay absolute and correlate to the active iteration. Repeating
source ancestors must match that iteration exactly. Collection-owned reads lower
to an explicit Source `frame` and a frame-relative compiler path, including
reads in descendant target scopes. Root reads outside a nonempty collection
prefix are refused when the collection schema can shadow that path (including
an empty repeated prefix or any declared terminal node). A schema group can
materialize as scalar null/absence, so terminal groups are conservatively treated
as shadows across structured formats, regardless of their nullability flag.
Bind those root fields outside the iteration. The
pinned unowned Source vocabulary has no uniterated primary-root selector;
`frame:[]` selects an actual empty-path iteration, not the document root.
Noncolliding root reads can use the compiler's outward lookup. Types and nullability must
be compatible. Integers are canonical signed 64-bit text, floats use finite JSON
numeric syntax, booleans are lowercase `true`/`false`, and null has no text.
Every required JSON target child, every ordinary singular XML element/text, and
every declared EDI/tabular positional child requires an explicit binding or
group scope. Optional JSON/XML children can remain absent.

Lowering emits `add_scope`, `set_iteration` with a primary-source
collection (the native `Source` iteration variant), and `bind`. Each binding gets
its own monotonically increasing invariant-decimal graph ID, even when two
bindings read the same source. Scope indices follow explicit child order. IDs
and serialized bytes are deterministic across parse/compile/replay.

The executable intersection admits source and constant nodes. Preview can also
execute Position, but the pinned `ferrule.edit.v1` expression union cannot create
it; v2 therefore refuses Position authoring explicitly. Calls, existing arbitrary
node references, expression text, sorting, filtering, generated sequences,
lookups, named endpoints, native blobs, schema imports, defaults, value
constraints and extension-data objects are rejected.

## Bounds and immutability

- Application and compiler documents: independently 65,536 bytes.
- Combined source/target schemas: 512 nodes, maximum 12 child edges.
- Bindings: 512 total; emitted operations: 1,500.
- JSON syntax: depth 64 and 16,384 values; no comments or trailing commas.
- Names/path components: 256 UTF-8 bytes; namespaces: 1,024 bytes.
- Constant text: 4,096 UTF-8 bytes, valid Unicode.
- Every object rejects duplicate, unknown and missing properties.
- Collections are bounded defensive copies exposed read-only; schema/value/scope
  objects have no mutable setters. Output byte copies are defensive.

## Verification

`MappingCreationFormatsTests` uses `FerruleInspectionBackend.AuthorAsync` against
the real portable compiler, then sends the exact resulting native bytes and
sample through the real authored-preview framing. It checks native determinism,
compiler validation, output digest/count, and actual mapped values.

Set `BRAID_AUTHORING_TEST_EXECUTABLE` and
`BRAID_AUTHORED_PREVIEW_TEST_EXECUTABLE` to the qualified portable examples, then
run with SDK 10.0.112:

```text
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --no-restore -m:1 --filter "FullyQualifiedName~MappingCreation|FullyQualifiedName~MappingAuthoringBackendTests"
```

The fixtures cover all 36 source/target pairs, two changed fields on every pair,
complete JSON-to-X12/EDIFACT envelopes, composite constants, real EDI escaping and
bad count/control refusal, CSV/TSV quotes/BOM/options, JSON typed constants/nulls,
nested source iteration, tabular row iteration, and strict admission/budgets.
Fixtures also cover XML text, namespaces, attributes and optional elements,
frame-pinned reads in nested target scopes, noncolliding root reads, repeating
XML children and terminal-group shadow refusal across JSON, XML, X12 and EDIFACT.
