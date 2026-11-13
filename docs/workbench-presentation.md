# Workbench presentation

The compact workbench separates structural roles, operation states and selection.
Their colors are tokens in `apps/web/src/style.css`; `semantic-tokens.css` applies them. Structural accents
identify incoming data, rules, results and supporting details. They are not
validation signals. Forest green identifies selection; an operation's status is
a separate labeled chip.

## Colour tokens and themes

Every colour in the web app is a custom property defined once in
`apps/web/src/style.css`: page and surfaces, text, borders, accents, status,
search highlight, focus rings, overlays and shadows, plus the `--wb-*` structural
and status tokens. Component styles use only `var(--token)`; do not add literal
colours. Token names describe the role (`--text-muted`, `--border-warn`,
`--surface-danger`). Numbered variants (`--text-muted-3`) keep each existing
light shade exact; give a new style the base token unless it needs a distinct
shade. `--primary` fills primary buttons, which carry `--text-on-accent`.

`[data-theme="dark"]` overrides the same tokens and sets `color-scheme: dark`, so
native controls and scrollbars follow. Keep text at 4.5:1 and control borders and
focus rings at 3:1 against the surfaces they sit on; `tests/theme.spec.ts` checks
the key pairs. The account menu offers Light, Dark and System (the default, which
follows `prefers-color-scheme` live). The choice is stored as
`weft.theme.v1` in local storage. `public/theme.js` applies it from `<head>`
before the first paint, as a same-origin file because the CSP admits no inline
script; `theme-preference.ts` owns it after start-up. Browser tests and the
documentation screenshots run with the light colour scheme and no saved choice.

## Vocabulary and status ownership

The mapping editor labels provisional local validation separately from worker
checkpoints. A draft save creates a version; it does not deploy or activate it.
Conflicts and uncertain saves remain explicit, and only an acknowledged commit
navigates to the saved revision. Editing status uses the shared labeled chips.

`presentation.ts` maps workflow identities to primary labels. Keep API values,
route names, request identities and payload contents unchanged. Keep **Regression
tests**, the short **Tests** navigation label, and **Diff**.

| Source identity | Primary label |
| --- | --- |
| `mapping` | Data mapping |
| `preview` | Try sample |
| `revisions` | Versions |
| `compare` | Compare |
| `builds` | Prepare |
| `releases` | In use |
| `audit` | Change history |
| `diagnostics` | Issues |
| input / output dock | Incoming data / Result |
| lineage / evidence | Data flow / Supporting details (Details in compact tabs) |

`StatusChipComponent` takes a presentation value, not arbitrary display text.
State adapters compare exact protocol codes. Unknown codes are unavailable, not
successes, failures or cancellations. Finishing execution is not a comparison
pass. Preparing a version is not proof it is approved, verified, eligible or in
use. A missing capability is not proof of denied permission.

A successful sample run is not a regression comparison pass. Missing expected
results, unsupported comparisons and unsuccessful execution retain separate
labels. Browser connectivity is also separate from server/session state: the
offline notice reports only the browser's connectivity observation and never
retries an operation or claims a previously sent operation stopped.

Sign-in status keeps local recovery, pending actions and unconfirmed revocation
separate from authenticated success. Upload state markers use the existing
admission and acknowledgement values; saving a file is not testing it or putting
it in use.

Exact IDs, hashes, enum values and file metadata remain available as technical
details. Do not humanize payload values or infer friendly business field names.

`MetadataValueComponent` formats file sizes and timestamps with an explicit UTC
label. Its keyboard-accessible disclosure preserves the exact byte count or
original timestamp, including fractional precision and offset. Missing or invalid
metadata is unavailable rather than invented. These formatters never handle
sample contents, expected results or the test's fixed clock setting.

## Action confirmation

Important preparation actions first capture their exact immutable operation in
page memory, then show `ConfirmationComponent`. Opening or dismissing the dialog
does not send that operation. The dialog states the selected mapping version,
known environment reference/version, request reference, intended effect and
limits of recovery after leaving the page. It never invents an environment name
or claims that preparation changes the version in use.

Confirmation dispatches that same captured frame through the existing fresh
admission check. Revision, identity or access invalidation clears the pending
dialog. Dismissal returns keyboard focus to its trigger; initial focus is on the
safe action. Stopping local request tracking is distinct from requesting server
cancellation and keeps that distinction in its confirmation.

## Layout preferences

`LayoutPreferencesStore` persists only a normalized versioned allowlist in
`weft.workbench.layout.v1`: density, panel sizes/collapse state, table widths,
optional rule/type column visibility, sort direction/key, grouping mode and key
column pinning. Reset layout restores both frame and table defaults.

Never serialize component state. Resource IDs, text searches, expanded group
keys, selection, private samples, operation/recovery state and credentials are
not layout preferences. Unavailable browser storage, invalid JSON, oversized
records and unknown versions fall back to defaults without blocking access.
The default row height remains 28 px, with 32 px comfortable and 24 px dense.
Key-column pinning yields on narrow screens so it cannot cover the other fields.
Keep a minimum scrollable table viewport when enlarged text wraps the controls;
horizontal containment alone is not proof that keyboard users can reach the rows.

## Investigation trail

The trail starts only from the selected run's evidence-backed affected-field
links. It retains the run/case version and visited field/rule references in
memory within the authorized revision page. It does not persist them as layout.
Returning to the test focuses that same result; it does not submit another run.
Changing the selected case/version/run, losing access or retiring the revision
binding clears incompatible trail state. Late callbacks cannot restore an old
binding. A declared data-flow link does not prove an observed runtime value.

## Copy inventory map

Review visible template text, TypeScript-generated text, accessible names,
tooltips and confirmation bodies together. Generated API declarations are
protocol vocabulary, not copy to rewrite.

Use Angular's template parser for external and inline templates, and the
TypeScript parser for generated messages and label maps. Include containing
state expressions, source locations and a source-file manifest. Plain HTML text
extraction misses Angular control-flow branches and inline templates. Inventory
bindings as source expressions, not as sampled user values; never rewrite
payloads, field keys, identifiers or protocol codes as part of a copy pass.
Collecting the inventory and approving/applying each wording change are separate
steps.

| Surface | Copy owners |
| --- | --- |
| Workflow navigation, command palette, inspector and dock | `workbench-frame.component.*`, `workbench-model.ts`, `presentation.ts` |
| Mapping filters, columns, sample labels and declared data flow | `mapping-table.component.*`, `mapping-table-model.ts`, `sample-value.component.ts`, `entity-inspector.component.ts` |
| Version context, history, unavailable capabilities and evidence trail | `catalog/revision-page.component.*`, `catalog/catalog-shell.component.ts` |
| Mapping explanation, controls, provenance and validation | `mappings/*.component.ts`, `catalog/reports/*.component.*` |
| Comparison selectors, missing reports and exact differences | `catalog/comparison/*.component.*`, `revision-comparison-view.ts` |
| Saved test editor, execution results, original-request recovery and dialogs | `catalog/regression/revision-regression.component.*` |
| Preparation, environment, approvals and original-request recovery | `catalog/builds/revision-builds.component.*` |
| Sample inputs, execution state, output handling and cancellation | `catalog/previews/*.component.*`, `preview-state.ts` |
| Project/mapping forms, permissions and route-level failures | `catalog/*page.component.*`, `catalog/catalog-*.ts` |
| Upload admission, unconfirmed submissions and leaving-page warnings | `catalog/uploads/*.component.*`, `native-*-state.ts` |
| Sign-in, access checking and server/local sign-out distinction | `auth/*.ts` |
| Question entry, grounded answers, citations and provider availability | `chat/*.ts`, `chat/*.html` |

## Verification boundaries

Exercise dense and narrow layouts, keyboard navigation, focus return, resizing,
200% text scaling, selected/hover states and contrast using the actual rendered
styles. Keep non-color labels/icons and exact expected/actual data visible.
Controlled browser fixtures verify presentation and privacy regressions; they do
not establish real API, database, worker or deployment behavior. Qualify those
workflows separately. Never use the design prototype's simulated actions as
product evidence.
