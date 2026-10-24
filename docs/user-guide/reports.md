---
id: reports
title: Reports, coverage and diagnostics
order: 7
summary: Read a revision's explanation and validation, generate them, and understand coverage and diagnostics.
---

Weft saves two reports for each revision. They are independent of each other,
and each belongs to that exact revision.

- The **explanation** shows how each result field is filled: which incoming
  fields a rule reads, its conditions, defaults and parameters, and the
  controls around it.
- The **validation** is the mapping engine's check of the file. It says whether
  the file is valid and lists the problems it found.

Both appear in the **Data mapping** view of the [workbench](workbench.md). The
headings at the top show each report's state, for example
**Explanation: Successful result saved**.

## Generate a report

Opening a revision only reads the reports that are already saved. It never
creates one. If a report says **Not generated**:

1. Open **Report controls and evidence** at the top of the **Data mapping** view.
2. Choose **Generate explanation** or **Generate validation**.

While Weft works, the report says **In progress** and the page checks its
progress for you. Checking never starts another attempt.

If an attempt fails, the report says **Attempt failed** with the reason. Choose
**Try derivation again** to make another attempt. If a saved result exists but
can't be read, because it is missing or failed its integrity check, choose
**Repair saved result**.

Generating reports may be turned off in your workspace. You can still read
reports that were saved earlier.

## Coverage

Coverage says how much of the mapping the explanation could describe. It
appears in the status bar, under the mapping table and in the inspector.

- **complete**: every field and rule is explained.
- **partial**: some behavior can't be explained fully. Rules may list
  *possible* incoming fields instead of definite ones. The gaps apply to the
  whole mapping, including rows your filter hides.
- **unavailable**: the file has no usable explanation. Its validation can still
  be available.

Open **Missing mapping details** in the inspector to see each gap and where it
is in the mapping file.

The explanation describes what the mapping declares. It doesn't show values
from a real run, and a complete explanation doesn't prove the mapping produces
the output you expect. Use [samples](previews.md) and
[regression tests](regression-tests.md) for that.

## Validation

The validation header says one of:

- **Native mapping is valid**: the mapping engine found no problems.
- **Native mapping has validation issues**: it found problems. They are listed
  as diagnostics.
- **Validation has no validity decision**: the check ran but returned no
  verdict.

A long list of findings may be shortened. Weft then shows how many findings
there are in total and how many it returned.

Validation and coverage measure different things. A valid file can still have a
partial explanation, and a file with validation issues can still be explained.

## Diagnostics

Diagnostics are the individual messages in a report. Open the **Issues** tab in
the investigation dock to see them, grouped by report:

- explanation notices describe what limited the explanation;
- validation notices describe problems in the mapping file.

Each diagnostic has a severity, a message and, where known, its location in the
mapping file. When Weft can tie a diagnostic to exactly one field or rule, it
offers a button such as **Inspect linked field** that selects it in the table.
A diagnostic without a link still shows its location in the file.

## Links to a saved report

**Open saved explanation link** and **Open saved validation link** open the
revision with that exact saved result selected. Share them when you want
someone to see the same report you saw, even after a newer report is generated.
**Read current reports for this revision** returns to the current reports.
