---
id: previews
title: Try a sample
order: 9
summary: Run a revision on sample documents and read or download its output.
---

A sample preview runs one revision on documents you provide and shows the
result. Runners, publishers and administrators can run previews. Use samples,
not production data.

Previews run on the exact revision you are viewing. Results are kept for a
limited time, shown under **Kept until**, and only you can read them.

## Run a preview

1. In the workbench, open the **Try sample** view.
2. Choose the **Preview admission** for the mapping, as described below.
3. Under **Input documents**, paste the **Primary input** or choose a
   file. A chosen file is sent exactly as it is.
4. If the mapping reads more than one document, choose **Add named input** for
   each extra document and enter its **Source name**, the name the mapping uses
   for it.
5. Under **Parameters**, add any parameters the mapping needs, with their type
   and value. Without parameters, the mapping's defaults apply.
6. Under **Outputs**, choose **All outputs**, **Primary output only** or
   **One named output**.
7. Choose **Run preview**.

**Legacy JSON** is the default admission policy.
For mappings with explicit JSON, X12 or EDIFACT boundaries, choose **Explicit
document boundaries** under **Preview admission**. Paste or choose the document
bytes; the mapping's embedded boundary settings select the format, not the file
extension. Missing or unresolved EDI configuration is refused rather than loaded
from another file.

For a flat CSV or TSV mapping, choose **Explicit CSV / TSV (saved native
dialect)**. This option appears only when your workspace offers CSV previews.
The mapping's saved delimiter, quoting and header settings determine the format;
neither the filename nor the pasted text selects it. CSV/TSV accepts exactly one
primary input and one primary output, with **All outputs** or **Primary output
only** as the target. Named inputs and named-output targets are not supported.
An empty text box or a zero-byte file submits an explicitly present empty input,
not a missing document. Other preview profiles still require nonempty input.

The preview waits in a queue, then runs. The page checks its progress for you.
Choose **Cancel preview** to stop it.

A preview can take up to 16 input documents, with at most 8 MiB of input in
total.

Under document admission, each EDI document is limited to 2 MiB, 32,768 segments
and 16 KiB per segment. JSON documents are limited to 64 nesting levels.
X12 segment display requires a complete ISA service declaration. Headerless X12
stays as raw text instead of guessing separators; downloads always retain the
exact bytes. X12 interchanges may declare different separators; each complete
ISA controls its own interchange. Explicitly configured input separators must
match every interchange.

## Read the result

When the preview finishes, braid shows its status, how many records it wrote and
a table of output documents. For each output, choose **Show** to read it on the
page (long outputs show only their beginning) or **Download** to save the exact
bytes.

EDI outputs with declared syntax open as a numbered segment view (up to 2,000
segments from the first 256 KiB); headerless X12 opens as raw text. Downloads keep
the exact bytes and use the recorded format's media type and `.x12` or `.edi`
extension.

CSV/TSV outputs open as a **Table** when their recorded native dialect is valid
and the document fits the browser's display limits. Choose **Raw text** to see
the original text instead. If the dialect is unavailable, the bytes are invalid
or a display limit is exceeded, braid explains why the table is unavailable and
keeps a bounded raw text or hex view. Downloads always keep the full original
bytes, recorded media type and `.csv` or `.tsv` extension. See
[document display limits](workbench.md#csv-and-tsv-document-display).

If the preview failed, braid shows the reason, a message from the mapping
engine and, where known, the input or field it concerns.

A successful preview only shows that the mapping ran. It isn't compared with an
expected result. To check output against an expected result, save a
[regression test](regression-tests.md).

Choose **Start a new preview** to clear the form for another run.

## Save a CSV or TSV preview as a test case

Publishers and administrators can save a successful CSV/TSV preview when the
workspace also offers the CSV regression runtime. The save controls require the
current preview's original submission and its single output with a recorded
native dialect.

1. Review the output, using **Show** or **Download** as needed.
2. Under **Save this CSV preview as a test case**, enter a **Test name**.
3. Select **I reviewed this output and accept its exact bytes as the expected
   baseline.**
4. Choose **Save preview as test case**.
5. Open **Tests**, choose **Load regression cases**, and run the saved version.

Saving verifies the output bytes and retains the preview's inputs, parameters,
evaluation time, runtime and returned output dialect. It creates an explicit
expected baseline using exact-byte comparison; a successful preview never becomes
a baseline automatically. To use ordered row/cell comparison instead, edit the
case as a new version in [Regression tests](regression-tests.md#csv-and-tsv-comparison).

## Reading FlexText and EDI output

For FlexText, expand **Saved FlexText layout (read only)** to see the saved
output settings alongside the raw text. A present, zero-byte output is labeled
as empty; it is not a missing artifact. [Synthetic FlexText example](../images/formats/flextext-saved-layout.png).

For X12 and EDIFACT, **EDI segments** marks each interchange in document order.
Switch to **Raw text** to see the original separators. A truncated byte window
is not presented as a complete segment list. Neither view changes the bytes
used for download. [Synthetic EDIFACT example](../images/formats/edi-interchanges.png).

## If you reload the page

This tab remembers your last preview of the revision, so after a reload braid
shows it again. If an earlier preview was sent but never confirmed, braid says
so: submitting the same inputs again returns that preview instead of starting a
second one. Choose **Discard earlier submission** to run different inputs
instead.
