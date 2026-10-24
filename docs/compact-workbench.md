# Compact revision workbench

The revision page uses a full-width data canvas, a compact application rail,
a contextual inspector and a shared investigation dock. The displayed revision
is immutable: opening a workflow tab does not follow the mapping's current head.
The head marker describes the metadata read, not a continuously refreshed claim.

## Navigation and inspection

- **Ctrl+K** opens the command palette. It includes workflow tabs and freshly
  read project/current-project mapping choices and recently opened mappings.
  Recent history retains only six mapping identifiers in tab memory; names and
  current access are read again before they appear. Opening a choice does not grant
  access; its destination performs its ordinary catalog admission.
- Mapping rows group by output or parent structure. Search covers the declared
  source, rule, target and related controls. Groups collapse independently; search
  expands matching groups. Sorting is stable. Large mappings page in groups of
  100 connections instead of creating an unbounded DOM.
- Arrow keys move field focus; Enter selects a field and Escape clears it.
  Source and target remain visible, while transformation and type columns can be
  hidden. Column and panel separators support pointer drag and arrow keys.
- The inspector follows the workflow. Its narrow-screen toggle overlays the
  canvas rather than shrinking the table. The dock and inspector can collapse.
- Compact, comfortable and dense settings use minimum row heights of 28, 32 and
  24 CSS pixels. Content can increase a row's height. Text remains relative to
  user scaling.

## Evidence and private state

Workflow changes hide, rather than destroy, their active components. Selected
cases, runs and original pending operations therefore remain available across
tabs. A revision-page lifetime owns the selection. Scope, actor, session or access
invalidation retires its epoch, so a late callback cannot revive the selection
after leaving and returning to the same scope. Layout values are kept separately
from evidence; no workbench preference stores sample bytes, tokens or requests.

Input, Output and Diff use authorized saved regression evidence. Reading a run
loads its exact case version when necessary. Draft inputs are not saved evidence.
Cases and runs from another revision provide an explicit link to that pinned
revision rather than showing private content beneath the current revision. A
mismatched recovery response does not discard the original pending request.
The diff compares original decoded bytes and labels expected versus actual;
reviewing it never replaces the baseline. Preview retains its independently
verified run and output download controls.

For JSON documents, the diff can link a concrete changed location to a field only
when one output boundary and one matching projected field identify it. Duplicate
keys, invalid JSON, oversized inputs or ambiguous fields produce no guessed link.
An array wildcard can match a concrete index only when the rest of the path
uniquely identifies the field. Byte offsets alone never identify fields.

Sample-value mode shows saved inputs and successful observed outputs beside
definitions, labeled with the case version, run and pinned revision. It indexes
bounded JSON lexically so integers above JavaScript's exact-number limit, decimal
spelling and negative zero are preserved. Missing, unsupported and unavailable
values remain distinct. Dependency declarations are not intermediate execution
traces. The inspector retains coverage gaps and native evidence.

## Operations and availability

Existing report, preview, regression and build APIs still own authorization,
validation, request recovery and mutation semantics. The workbench does not
automatically derive reports, submit runs, approve builds or accept baselines.
Successful builds are not active releases. The Releases view separates active
state, candidate metadata and qualification/activation gates. Where the connected
browser API has no release or audit reader, it states that limitation and exposes
no simulated activation or rollback action.

## Checks

`playwright.workbench-model.config.ts` covers the pure selection and JSON rules.
`playwright.workbench.config.ts` covers the integrated Angular interactions using
explicitly synthetic API boundary fixtures; these do not establish backend
authorization or persistence qualification. The existing real-API suites remain
the separate check for those contracts. Set `WEFT_CHROMIUM_EXECUTABLE` to a verified
installed Chromium browser when the matching Playwright browser is unavailable.
The workbench suite uses port 5181 and leaves the local viewer on port 4102 alone.
Its development server, like the chat and comparison suites', sends unrouted API
requests to a closed loopback port (`proxy.synthetic.conf.json`), never to a local
instance on 4100.
