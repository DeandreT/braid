# Exact-output regression comparison

`Weft.Application.Regression` provides the pure output-comparison boundary for
persisted regression cases. It does not read storage, authorize access, execute
mappings or persist runs. A future case/run service must supply verified bytes
from the exact authorized case and execution; constructing an observation does
not establish that execution occurred.

The first policy is `weft.regression.exact-bytes.v1`. It matches outputs by exact
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

Persistence, expected-output approval, runtime identity, run diagnostics,
retention, API/UI and actual worker replay belong to the later P10 increments.
