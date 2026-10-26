# Unified workbench

Authenticated catalog, mapping, search, operations, administration, notifications,
guide and conversation routes share a full-width shell, navigation rail, account
menu and Search / Go to palette. Sign-in and invitation entry remain separate
identity boundaries. Existing URLs and admission checks keep their meanings.
The revision page uses a data canvas and a bottom inspector/evidence tray. The displayed revision
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
- The inspector follows the workflow in the bottom tray, alongside Issues,
  Incoming data, Result, Diff and Activity. The tray collapses when not needed.
  Its height is bounded to 40% of the available work region while reserving 240px
  for the canvas. Small or short regions use a focused details view with a Back
  action instead of an overlay.
- Compact, comfortable and dense settings use minimum row heights of 28, 32 and
  24 CSS pixels. Content can increase a row's height. Text remains relative to
  user scaling. Comfortable (32px) is the default.

The right conversation is one mounted surface across route changes. Hiding it
does not cancel a stream, send a draft or discard recovery. Its width starts at
360px, ranges from 300–560px and is clamped to preserve the main content region.
At phone width it becomes a focused panel rather than covering the canvas.
The labeled separator supports pointer drag, Left/Right (10px), Shift (20px),
and Home/End. Width and open-state preferences are harmless per-user layout
data; conversations, drafts, tokens and request recovery never enter storage.

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
`playwright.unified-workbench.config.ts` covers the shared shell and preserved
route, form, chat and workbench workflows on a synthetic API boundary at port
5186. `WEFT_UI_CAPTURE_DIR` optionally retains its responsive screenshots.
`playwright.workbench.config.ts` covers the integrated Angular interactions using
explicitly synthetic API boundary fixtures; these do not establish backend
authorization or persistence qualification. The existing real-API suites remain
the separate check for those contracts. Set `WEFT_CHROMIUM_EXECUTABLE` to a verified
installed Chromium browser when the matching Playwright browser is unavailable.
The workbench suite uses port 5181 and leaves the local viewer on port 4102 alone.
Its development server, like the chat and comparison suites', sends unrouted API
requests to a closed loopback port (`proxy.synthetic.conf.json`), never to a local
instance on 4100.
