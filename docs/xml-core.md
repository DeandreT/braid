# XML safety and metadata core

The XML core supplies physical-document validation and saved-boundary metadata
admission. It does not enable XML previews, regression cases, compiled builds or
mapping endpoints. Those integrations require their own versioned contracts and
qualification.

The separately configured [native XML preview and regression workflow](xml-previews.md)
reuses this core. Its regression cases compare exact bytes; it enables neither
canonical comparison nor compiled XML execution.

## Safety profile

`weft.xml-safe.v1` accepts one nonempty XML 1.0 document encoded as strict UTF-8,
with an optional leading UTF-8 BOM. DTDs are prohibited, external resources are
never resolved, and declarations cannot select another encoding. The document
limit is 8 MiB, with a maximum physical depth of 64, one million physical nodes
and 256 attributes per element. A complete input or output set shares the byte
limit and retains the existing 16-input/64-output bounds.

The native core performs bounded event validation before the pinned structured
XML parser and requires closed structured schemas and an admitted emission plan.
The .NET core uses `XmlReader` with DTD processing prohibited and no resolver.
Neither a physical parse nor a metadata projection establishes that an arbitrary
mapping is supported. Limits bound acceptance; they do not promise constant
memory or eliminate parser intermediates.

Physical element and attribute name components, namespace prefixes and
processing-instruction targets use the BMP name-character set of the pinned
.NET XML reader. Processing-instruction targets cannot contain colons. The
native core uses frozen, attributed ranges so it does not accept newer XML name
characters that the generated .NET path would reject. Text, attribute values
and namespace URI values are not restricted by that name table.

## Saved boundary metadata

`weft.xml-boundary.v1` records the safety profile and optional output schema
hints. Inputs cannot carry output hints. Hint locations are literal output
metadata, not files or URLs to retrieve. Unknown fields, duplicate fields,
conflicting formats and unsupported saved options are refused.

Every occurrence of metadata in the declared input/output set is charged to one
32 KiB encoded-JSON budget. Counting includes property names, punctuation and
escaping; repeated values are not discounted. Enclosing request, response and
build-header limits remain separate and must also be enforced when this core is
integrated.

Both implementations charge the larger of the native JSON representation and
the pinned .NET default-encoder representation. Native minimal escaping must not
admit a declaration that exceeds the .NET budget. Metadata, schema-hint and
namespace/location records require named JSON objects, never positional arrays.

The shared `engines/ferrule-host/tests/corpora/xml-safety-cases.json` corpus feeds
the same bytes to each physical-safety implementation. These focused fixtures do
not replace exhaustive parser compatibility, resource-limit or execution gates.

## Required integration boundaries

Keep JSON, EDI and CSV serialization and execution paths unchanged. XML needs
explicit default-off host admission, complete native/generated metadata parity,
new private envelope and persistence admission, safe display, and real execution
tests before advertisement.

## Dormant canonical comparison core

The internal .NET core uses policy `weft.regression.xml-c14n10-comments.v1`:
inclusive W3C Canonical XML 1.0 with comments, for complete documents only. It
directly constructs `XmlDsigC14NWithCommentsTransform` from the exact
`System.Security.Cryptography.Xml` package reference `[10.0.14]`, sets its resolver
to null, and verifies the algorithm URI. It loads a whitespace-preserving, safe
`XmlDocument`, never a raw stream, node subset or configurable transform chain.

Every present document, including identical bytes and unmatched keys, passes the
physical safety scan before DOM construction. A separate namespace-domain scan
refuses nonempty relative namespace identifiers and supplementary characters in
namespace URI values. Empty default-namespace resets remain valid. Supplementary
ordinary text and attribute values remain allowed. These are refusals, not URI
normalization or prefix rewriting.

Internal snapshots own the original payload and digest. Both complete sets are
charged against `RegressionComparisonLimits.ComparedBytes` before parsing, and
each side retains the XML 8 MiB/64-output ceilings. The caller must explicitly
supply its smaller original-byte allowance, canonical per-document allowance,
and combined canonical allowance. The core installs no canonical budget defaults.
It compares canonical bytes, not digests, with cancellation polls and bounded
reads; canonical byte offsets and lengths are distinct from original-byte facts.
Invalid XML, unsupported domains or resource/canonicalizer failures yield an
incomplete `NotEvaluated` result with no partial differences or canonical totals.
Only fixed reasons, trusted output keys and bounded counts enter diagnostics.

The package eagerly copies the DOM and allocates canonical text and UTF-8 output
before exposing its stream. Bounded reads and post-transform length checks limit
acceptance and retained comparison material, not peak allocation. Cancellation
checks before/after synchronous `LoadInput` and `GetOutput` cannot interrupt those
calls. Enabling this policy requires an independently qualified enclosing worker
that can enforce the existing memory/CPU/deadline limits and terminate/reap these
calls, along with selected finite canonical budgets and official W3C vectors.
No new worker topology or process limits are selected by the core.

`XmlComparisonLiveRuntimeTests` uses the existing live service-compile prerequisites
to exercise the packaged .NET runtime behind the read-only launcher: dependency
startup, complete-set comparison, unsafe-input refusal, explicit test budgets and
pre-decode deadline/cancellation cleanup. Its pre-decode checks do not establish
interruption during synchronous `LoadInput`/`GetOutput`, peak-allocation containment
or suitable production canonical budgets. Those remain separate enablement gates.

This core is internal and has no host registration or existing comparer-dispatch
route. Canonical comparison does not replace exact-byte build evidence. The
complete design is tracked with [#330](https://github.com/DeandreT/weft/issues/330).
