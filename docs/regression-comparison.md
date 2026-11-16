# Regression output comparison

`Braid.Application.Regression` provides the pure output-comparison boundary for
persisted regression cases. The case/run service supplies verified bytes from the
exact authorized case and execution.

The first policy is `braid.regression.exact-bytes.v1`. It matches outputs by exact
logical identity: one primary identity and case-sensitive named identities.
Names are neither paths nor normalization instructions. Incoming list order
does not affect the result. The deterministic output order is primary first,
then named outputs in ordinal string order.

Bytes are compared without text decoding, JSON parsing, whitespace changes or
number normalization. Encoding, byte-order marks, newlines and numeric spelling
therefore remain significant. SHA-256 digests and lengths accompany differences,
but matching digests do not replace byte comparison. No raw byte excerpts enter
comparison results or diagnostic text.

## Outcomes

- `Passed`: a successful execution and a nonempty expected output set match
  exactly, with no missing, unexpected or changed output.
- `Failed`: comparison completed and found at least one difference. Missing,
  unexpected and changed outputs remain separate categories.
- `NotEvaluated`: the requested policy is unsupported, execution did not
  succeed, or no baseline was supplied. None of these outcomes is a pass.

The original execution status remains in the result. Difference lists can be
bounded, but the total difference count and failed outcome include every output
identity examined. `Truncated` never means the omitted outputs matched.
`FirstDifferentByte` is a zero-based offset into a changed output; a strict
prefix difference points to the length of the shorter value.

## Ownership and bounds

`RegressionOutput.Capture` owns a copy of at most 16 MiB. Its byte accessor
returns a copy. `RegressionOutputSet.Capture` rejects duplicate identities,
owns its list, and admits at most 128 outputs and 32 MiB per side. Empty output
bytes are valid; an empty baseline is not sufficient for a passing regression.

Comparison reads matching output pairs in 64 KiB chunks and charges bytes on
both sides against an explicit budget, at most 64 MiB. Metadata-only missing or
unexpected output decisions do not consume that byte budget. An insufficient
budget or malformed input fails categorically, rather than returning a partial
pass. Cancellation is checked before work, between outputs and between byte
chunks. An independent output limit bounds retained differences to at most 256.

## Declared document comparison

`braid.regression.edi-segments.v1` requires all three explicit boolean choices:
`compareSeparators`, `ignoreControlNumbers`, and `ignoreDates`. JSON outputs
still use exact byte comparison. X12 and EDIFACT outputs use ordered lexical
segments, elements, repetitions and components. Output media types must match;
neither filenames nor content sniffing select a family. Missing and unexpected
logical outputs remain differences.

The comparer requires complete scalar envelope controls and counts before any
comparison masks apply. Unescaped composites or repetitions are invalid in those
fields; released literal separators remain part of the complete reference.
The comparer validates X12 ISA/GS/ST/SE/GE/IEA and EDIFACT
UNB/UNG/UNH/UNT/UNE/UNZ envelope references and counts before masking. Standalone
EDIFACT messages and X12 TA1 interchange acknowledgements are admitted. Multiple
interchanges, functional groups and messages retain their original order.
EDIFACT release characters are decoded lexically; service separators are compared
unless explicitly disabled. This is a bounded lexical/envelope validator, not a
trading-partner implementation-guide validator.

Control-number masking covers X12 ISA13, GS06, ST02, SE02, GE02, IEA02, TA101,
AK102, AK202 and IK202; EDIFACT UNB05, UNG05, UNH01, UNT02, UNE02, UNZ02 and
UCI01/UCF01/UCM01. Acknowledgement response/action codes remain significant.
Date masking covers X12 ISA09/10, GS04/05, BHT04/05, DTM02/03, G6202/04 and
TA102/03; EDIFACT UNB04, UNG04 and the date/time value component of DTM01.
Other values remain significant, including qualifiers and date-format codes.
Masking does not remove elements, repetitions or components.

Invalid documents produce `NotEvaluated` with an `invalidDocument` reason and
a payload-free expected/actual diagnostic location. Differences carry one-based
interchange, segment, element, repetition and component locations; zero denotes
a whole segment or separator set. Parsing admits at most 2 MiB per EDI output,
32,768 segments, 16 KiB per segment and 65,536 lexical values. Resource-bounded
refusals cannot become passes.
