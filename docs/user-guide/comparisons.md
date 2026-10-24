---
id: comparisons
title: Compare revisions
order: 8
summary: Compare the saved explanations and validations of two revisions of the same mapping.
---

A comparison shows what changed between two revisions of one mapping: fields,
rules, parameters, controls and validation findings that were added, removed or
changed.

## Start a comparison

- On a mapping page, choose **Compare revisions**. The newest revision is placed
  on the right.
- In the workbench, open the **Compare** view and choose
  **Compare with another version**. The revision you are viewing is placed on
  the right.

Then choose the other revision from the list with **Use as left** or
**Use as right**. **Swap left and right** exchanges them.

The comparison starts when both revisions have a saved explanation. If one side
has none, Weft says so and links to that revision's report controls, where you
can [generate it](reports.md#generate-a-report). A comparison never generates
reports itself. Validation is compared when it has been saved for both sides.

## Read the result

Results are grouped by kind, such as fields or rules. Each item is one of:

- **Matched**: found in both revisions. Its changed aspects are listed.
- **Only in left** or **Only in right**: found in one revision only. A renamed
  field appears once on each side.
- **Ambiguous**: more than one counterpart is possible, so Weft doesn't pair
  them. The possible counterparts on each side are listed.

Matched items with no observed difference are hidden. Choose
**Show unchanged items** to see them.

## When the result is incomplete

Banners at the top tell you when the comparison can't be complete, for example
when one explanation has only partial [coverage](reports.md#coverage), some
items were left out to keep the result within limits, or some facts couldn't be
compared. Facts that couldn't be compared are marked with the reason, and are
never reported as equal.

Weft says no differences were observed only when both explanations are complete
and nothing was uncertain or left out. Even then, matching explanations don't
prove that the two revisions produce the same output. Run your
[regression tests](regression-tests.md) on both to check that.

## Share a comparison

Each comparison names the exact revisions and saved reports it used, under
**Cited evidence**. **Link to this exact comparison** opens an address you can
bookmark or share. It shows the same comparison later, even after newer
reports are generated.
