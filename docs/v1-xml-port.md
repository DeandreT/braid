# Selective v1 XML port

**Historical port record, not current setup instructions.** Worktree paths,
uncommitted status, migration inventory and runtime gaps below belong to this
checkpoint. The original evidence and anchors are retained. For current native
preview/regression behavior use [XML previews](xml-previews.md); for authoring use
[typed creation formats](mapping-creation-formats.md). [XML design](xml-document-design.md)
remains a proposal where it describes future compiled XML support, not deployed
capability. Later combined evidence is in [v1 integration](v1-integration.md);
[ROADMAP](../ROADMAP.md) owns future work and deferred acceptance.

Worktree: `C:/example/checkout`, branch `feat/v1-xml-documents`.
Integration base: `59ec1f014c7aad1949935ba4c13bc71a15837223`.

Applied feature deltas:

- Core: `916a4ddd45ac561e709631395856f374c33ab06f` to `234d265b009ad880c4df3f2251df4c0f6f89f8dc`.
- Workflows: `b53a78c38aff1305a4195b21dec400c6415798f3` to `3dbf2d052d35a7046f3f6546f27445caefede8b8`.

The port retains saved-revision XML preview and exact-byte regression, safe
metadata/byte admission, CLI/browser selection and explicit compiled-build refusal.
Lifecycle/archive, FlexText workflow, audit/quota and browser collector ancestry
were excluded. XML completion uses the base authorization and row-lock/lease
settlement checks. Draft subjects, checkpoints, CSV, intake and proposal paths
remain on the integration base's contracts. Preview and regression XML backends
have independent enablement. Canonical comparison remains dormant.

## Preservation

All 41 existing migration files were compared as raw Git blob bytes with the base
and match. The shared `engines/ferrule-host/core` tree has no diff from the base.
The new migration is `0042_xml_documents.sql`, SHA-256
`332e8d0c07f2b6dd53a0a6be26ec3c6cb79e9ace7d9287532211fb542472f012`.
Schema admission, migration inventories and generated contracts use that suffix.
Historical upgrade fixtures use separate test-only restricted SQL access; current
production preview admission still requires the XML schema even for legacy work.

## Validation on Windows, 2026-11-12

- .NET SDK 10.0.112: locked restore and full Release solution build passed, with
  zero build warnings/errors. The expanded focused run passed 629 tests, skipped
  68 and failed none after formatting and historical-fixture reconciliation.
  Additional database-only coverage checks XML cancellation, authority revocation,
  stale leases and populated draft conservation across the XML suffix; it requires
  the same unavailable PostgreSQL fixture (12 database test entries skipped in the
  final targeted run). Full-solution `dotnet format --verify-no-changes` and the
  final Release rebuild passed, with zero build warnings/errors.
- Node 22.23.3 / npm 12.1.0: clean locked installs, complete `npm run check`, generated
  contract checks and production build passed. Initial bundle warning: 655.72 kB
  against the 500 kB warning budget.
- Rust 1.98.1: shared-core locked test command built successfully (zero unit/doc
  tests in that crate). This is a build check, not native runtime qualification.
- Native-host locked tests cannot compile on Windows because `seccompiler`
  requires Linux APIs. No Docker or WSL distribution was available for Linux
  qualification. Formatting used the pinned Rust toolchain.

## P2 review follow-up: baseline visibility

`RevisionPreviewComponent.canSaveCase` now requires the runtime selected for the
completed preview's profile. An XML result requires `xmlRuntime`, independently
of whether `csvRuntime` is offered. Format selection is unchanged.
The behavioral regression tests exercise the rendered component through the
existing Playwright workflow fixture: XML regression enabled/CSV disabled exposes
the baseline controls, while CSV enabled/XML regression disabled hides them.
The enabled case also checks that naming and explicit approval enable Save.
These use authored HTTP responses, so browser success qualifies UI visibility and
interaction only, not native execution, actual regression persistence or database
authorization.

After this fix, the complete pinned `npm run check` and `npm run build` passed.
The production initial-bundle warning remains 655.72 kB against 500 kB.
The final focused Playwright run passed all 11 `csv-workflow.spec.ts` tests,
including both XML cases, with actual Chromium 156.0.8078.4 (Playwright revision 1248) on Windows. The initial missing-browser failure was resolved by installing
the locked package's required Chromium revision. Browser execution exposed stale
fixture responses: revision runs now include `subjectKind: "revision"` and
`draft: null`; the XML-only capability fixture omits CSV comparison policies.
After those fixture corrections, formatting and browser TypeScript checks passed
again. Isolated final browser output is under
`%LOCALAPPDATA%/Temp/opencode/weft-xml-p2-browser-passed`.

## Remaining runtime qualification gaps

Actual PostgreSQL migration/conservation, restricted-role admission, concurrent
completion/cancellation and database HTTP tests were skipped because the owned
database fixture was not configured. Linux native XML execution, sandbox/resource
containment and browser workflows backed by real native/database services were
not run. The actual-browser tests above use authored HTTP responses; they establish
the tested component visibility and CSV interactions, not end-to-end XML execution
or persistence. TypeScript compilation and authored protocol tests likewise do not
establish those runtime gates. Compiled XML
execution remains unsupported. No deployment or customer acceptance is claimed.

Changes are intentionally uncommitted. No remote Git operations or subagents were
used. Local check logs/TRX files are under the approved temporary OpenCode paths.
