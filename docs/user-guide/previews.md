---
id: previews
title: Try a sample
order: 9
summary: Run a revision on sample JSON documents and read or download its output.
---

A sample preview runs one revision on documents you provide and shows the
result. Runners, publishers and administrators can run previews. Use samples,
not production data.

Previews run on the exact revision you are viewing. Results are kept for a
limited time, shown under **Kept until**, and only you can read them.

## Run a preview

1. In the workbench, open the **Try sample** view.
2. Under **Input documents**, paste the **Primary input (JSON)** or choose a
   file. A chosen file is sent exactly as it is.
3. If the mapping reads more than one document, choose **Add named input** for
   each extra document and enter its **Source name**, the name the mapping uses
   for it.
4. Under **Parameters**, add any parameters the mapping needs, with their type
   and value. Without parameters, the mapping's defaults apply.
5. Under **Outputs**, choose **All outputs**, **Primary output only** or
   **One named output**.
6. Choose **Run preview**.

The preview waits in a queue, then runs. The page checks its progress for you.
Choose **Cancel preview** to stop it.

A preview can take up to 16 input documents, with at most 8 MiB of input in
total.

## Read the result

When the preview finishes, Weft shows its status, how many records it wrote and
a table of output documents. For each output, choose **Show** to read it on the
page (long outputs show only their beginning) or **Download** to save the exact
bytes.

If the preview failed, Weft shows the reason, a message from the mapping
engine and, where known, the input or field it concerns.

A successful preview only shows that the mapping ran. It isn't compared with an
expected result. To check output against an expected result, save a
[regression test](regression-tests.md).

Choose **Start a new preview** to clear the form for another run.

## If you reload the page

This tab remembers your last preview of the revision, so after a reload Weft
shows it again. If an earlier preview was sent but never confirmed, Weft says
so: submitting the same inputs again returns that preview instead of starting a
second one. Choose **Discard earlier submission** to run different inputs
instead.
