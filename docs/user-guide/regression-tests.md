---
id: regression-tests
title: Regression tests
order: 10
summary: Save test cases with expected output, run them against a revision and read the result.
---

A regression test saves sample inputs together with the output you expect. When
you run it, Weft compares the real output with the expected output using the
case's saved comparison policy. Exact bytes is the default; the separately
enabled CSV/TSV runtime also offers ordered row/cell comparison. Use test data,
not production data.

## Who can do what

- Everyone in the project can see the names of saved tests.
- Runners, publishers and administrators can open saved tests and run them.
- Publishers and administrators can save tests and new versions of them.

## Open the tests

In the workbench, open the **Tests** view. Weft loads the saved test cases
and your run history, and shows the default comparison. Use
**Filter cases** to find a case, and choose **Open** next to it to see its
inputs, parameters, test date and time, and expected outputs.

A case belongs to the revision it was saved with. A case saved with another
revision is marked **Other revision**; its details stay with that revision.

## Save a test case

For CSV/TSV, first [save a successful preview as a test case](previews.md#save-a-csv-or-tsv-preview-as-a-test-case),
or open an existing CSV case and choose **Edit as next version**. Its verified
native output dialect is required; choosing a file does not supply or guess it.

For a new JSON or declared-document case:

1. Choose **New test case**.
2. Enter a **Test name**.
3. Enter the **Test date and time (UTC or offset)**. The mapping sees this
   fixed time as the current time, so the test gives the same result whenever
   it runs.
4. Choose the **Target**: all outputs, the primary output or one named output.
5. Under **Input documents**, enter the primary input as text or choose a file.
   Choose **Add named input** for each extra document the mapping reads.
6. Under **Expected output documents**, enter the output you expect. Leave the
   name blank for the primary output; choose **Add expected output** for each
   named output.
7. Add any **Typed parameters**.
8. Choose **Save test version**.

Text is saved as you typed it; a chosen file is saved exactly as it is. Inputs
and expected outputs can each be up to 8 MiB in total.

A saved version never changes. To change a test, open it and choose
**Edit as next version**. Runs of the earlier version keep their results.

A test without an expected output cannot pass. CSV/TSV cases require exactly one
primary expected-output document. A zero-byte expected document is a real
baseline, not an absent expectation: keep the document entry and its saved
dialect even when its content is empty. CSV/TSV also accepts an explicitly empty
primary input, but no named inputs or named-output targets.

## CSV and TSV comparison

When offered, **Execution profile: CSV / TSV (saved native dialect)** uses the
revision's native CSV runtime. Choose one of its **Comparison policy** options:

- **Exact bytes and media type** compares the complete output bytes, including
  quoting, record terminators and any BOM. The recorded media type and native
  dialect must also match. A case saved from a preview starts with this policy.
- **Ordered CSV rows and cells** compares decoded cell strings in row and column
  order. Header rows are included, even when the mapping treats them as headers.
  Whitespace and embedded cell line breaks remain significant. Nothing is
  trimmed, converted to a number or padded; a missing trailing cell differs from
  an empty cell. Equivalent quoting, record-terminator syntax and an optional
  initial UTF-8 BOM do not themselves count as differences. The recorded media
  type and dialect must still match.

Row comparison reports the first differing one-based row and column. Invalid
CSV or UTF-8 reports whether the expected or actual document is invalid and its
row/column location, without including cell values in the diagnostic. An invalid
document is not a successful comparison.

Only exact-byte evidence can be used for [builds](builds.md). A row/cell match
does not qualify a build, even if the displayed tables look identical.

## Run a test

Open a case and choose **Run saved version**. The run uses the revision and
expected output that version was saved with, even if the mapping changes later.
Queued and running tests can be refreshed or cancelled.

A finished run shows one of:

- **Matches expected result**: every output matches under the saved policy.
- **Result differs from expected**: at least one output differs. Weft lists
  each differing output, with a byte offset for byte differences or a row and
  column for CSV cell differences.
- **Expected result needed**: the test has no expected output.
- **Could not run this test successfully**: the run failed, so there was no
  output to compare.
- **Comparison not supported**: Weft can't compare this kind of result.
- **Comparison not performed**: the comparison could not be evaluated, for
  example because a CSV/TSV document is invalid. Read the diagnostic before
  changing or rerunning the case.

Open the **Diff** tab in the investigation dock to see the expected and actual
output side by side. **Changed output locations** lists the result fields that
differ; choose one to see it in the mapping. Reviewing a difference never
changes the expected result.

The side-by-side view retains the original bytes and can show formatting changes
that ordered CSV row/cell comparison accepts. Use the run's saved policy and
row/column result for its verdict, not table appearance alone.

**Your run history** lists your own runs. Only you can see the results of your
runs. **Refresh run history** reads it again.

## If a save or run isn't confirmed

If Weft can't confirm whether a test was saved or a run started, it keeps the
original request and shows **Check previous save** or **Check previous run**.
Choose **Check saved version** or **Check original run** to see what happened,
or retry the original request. Retrying never creates a second case or run. See
[When a result isn't confirmed](unconfirmed-results.md).
