---
id: workbench
title: Work with a revision
order: 6
summary: Find your way around the revision workbench, its views, inspector and investigation dock.
---

Catalog, projects, mappings, operations, administration and the guide use the
same workbench shell. Opening a revision fixes the main work area's revision;
the shared conversation can have a different, explicitly labeled focus.
If a newer revision is uploaded while you work,
the page keeps showing the one you opened; the badge next to the version says
**a newer version is available** when it last checked.

## Layout

- **Views** along the top choose what you're working on. See
  [Views](#views).
- The **inspector** in the bottom tray shows details of what you selected. Its
  Rule, Data flow and Details tabs change with the view. **Hide inspector** gives
  the main view more room. Narrow screens use **Back to work** instead of an overlay.
- The **investigation dock** also holds issues, saved test data and differences.
  Drag its edge to resize it, or collapse it with the arrow button.
- **Show chat** opens the shared conversation on the right. Drag its left edge
  or use the width control to resize it. **Hide chat** preserves your question
  and conversation. On a phone, chat replaces the work area until hidden.
- The status bar at the bottom shows the revision's file status and how
  complete its explanation is.

Choose **Search / Go to** or press **Ctrl+K** (**Cmd+K** on a Mac) to search the catalog or jump to a view, a
project, a mapping in this project or a mapping you opened recently.

## Views

| View | What it's for |
| --- | --- |
| Data mapping | Read how each result field is filled. See [Reports](reports.md). |
| Try sample | Run this revision on sample documents. See [Try a sample](previews.md). |
| Versions | See the mapping's revision history and open another revision. |
| Compare | Open a comparison of this revision with another one. See [Compare revisions](comparisons.md). |
| Tests | Save and run regression tests. See [Regression tests](regression-tests.md). |
| Prepare | Approve and prepare a build of this revision. See [Builds and approvals](builds.md). |
| In use | What must be true before a revision is put in use. The workbench can't show which revision is in use. |
| Change history | A reminder that the workbench doesn't show the history of revisions put in use. |

## Investigation dock

| Tab | What it shows |
| --- | --- |
| Inspector | Details of the selected field, rule or workflow evidence. |
| Issues | The problems listed in this revision's explanation and validation. |
| Incoming data | The saved input documents of the regression test you selected. |
| Result | The output documents of the regression run you selected. |
| Diff | The expected and actual output of the selected run, side by side. |
| Activity | What the page has read for this revision. |

**Download exact bytes** saves a document exactly as Weft stored it. Long
documents are shortened on screen, but the download is always complete.

## CSV and TSV document display

CSV/TSV documents with valid recorded native dialect metadata open as a **Table**.
Choose **Raw text** to inspect their original text. Header settings come from
that metadata, not a filename or a guess at the first row. Cells stay in source
order without trimming, type conversion or padding missing cells.

The browser checks the complete document before showing a table, including rows
it will not display. Its limits are:

| Display limit | Maximum |
| --- | --- |
| Document bytes | 1 MiB |
| Encoded cell content | 64 KiB |
| Records, including a declared header | 10,000 |
| Columns per record | 256 |
| Parsed nodes, one per record and per cell | 100,000 |
| Data rows shown | 200 |

These are display limits, not execution limits. Native CSV execution allows up
to 8 MiB per document, 1 MiB per cell, 100,000 records, 256 columns and 1,000,000
materialized nodes; a run's configured limits may be lower. If the browser cannot
show a table because of its limits, invalid bytes or missing or invalid dialect
metadata, it explains the reason and keeps a bounded raw text or hex view.
Raw text shows at most the first 64 KiB; the hex fallback shows at most 1 KiB.
Downloads and regression comparisons use the original complete bytes, not the
displayed table or shortened text.

## Read the mapping table

The **Data mapping** view lists field connections: each **Incoming field**, the
**Rule** that uses it, and the **Result field** it fills.

- Type in the filter box to find an incoming field, rule or result field.
- Choose a result document to show only its fields, and group fields
  **By structure** or **By result document**.
- **Incomplete details** shows only fields whose explanation is incomplete.
- **Data flow** shows how the selected field is filled: incoming field, rule,
  result field. This is what the mapping declares, not values observed while it
  ran.
- **Sample values** shows values from the regression test selected in
  **Tests** beside the fields.
- **Columns** hides the rule and type columns, or pins the incoming field
  column.

Select a row to open its details in the inspector. With the keyboard, use the
arrow keys to move between rows, **Enter** to inspect a field and **Escape** to
close it. Large mappings are shown 100 connections at a time.

## Layout settings

The density button in the status bar switches between compact, comfortable and
dense rows. Panel sizes, column widths and density are remembered in this
browser. **Reset layout** restores the defaults. Weft never stores your
selections, sample data or requests as layout settings.
