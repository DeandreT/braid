# V1 CSV/TSV selective port

## Source and scope

- Integration base: `177b458edc282502b1575ae9a94ed2750554a512` (includes proposal storage contract `7b9e159c90d05f1828f6e6255b1a8e2108e0ae12`).
- Source feature delta: `35ed060b84c52923b3c9cf8dfb8d7d49024d97e1` to `916a4ddd45ac561e709631395856f374c33ab06f`.
- CSV implementation anchors: `0a31750922bef27277cc87caa5bc754bf7dc123e`, `f6e787ab47cdf7514e130ab28680059753fd88e2`, `23c8f174877bd6a34260e0a6285cb5d2dc7db54b`, `b1e102007f39b95c8158f0f67d1c03b778abc434`, `3f9abea793fd6021870a9b53c10e3363058fc406`. The final-tree delta includes their later fixes.

The selective three-way patch was reconciled with editor/proposal changes. It carries CSV/TSV preview, regression, build/runtime, CLI, table rendering, contracts, and tests without merging the audit/quota/browser-policy branches. The three source-delta edits to absent audit/quota documentation and tests were excluded.

The editor's shared Rust `core` workspace, draft preview subjects, nullable revision/native-object fields, retained checkpoint execution, chat proposal machinery, and shell/worker CSP remain in place. CSV tests use the base tree's Playwright harness; their format assertions remain. That is not a browser-policy collector qualification.

## Migration reconciliation

`0001` through `0040` are byte-identical to the integration base. CSV occupies `0041_csv_documents.sql`; XML `0042` and proposal persistence `0043` remain reserved for subsequent work.

The upstream CSV migration checksum was `67c4df0a46b765ac6031a4d9d5fe188e4caa0481279a4fc50a6689e4ded55219`. The integrated migration checksum is **`1d338dc7831f9079269c2ecfc2014492ab6e9402e1d28daf5eba301226d5f9f9`**.

The SQL changes preserve migration 0038's bounded direction-3 native checkpoint documents when extending the document constraint, restrict CSV runs to revision subjects, and use subject-aware authorization for metadata reads. Existing draft previews continue to use their original JSON/document profiles. Save a draft as a revision before using CSV preview admission.

Embedded resource names, ordered migration inventories, exact checksum assertions, and historical upgrade checks use the new sequence. A PostgreSQL regression test covers a populated prefix-40 draft checkpoint, upgrade, exact-byte conservation, replay, and a new draft preview after the CSV suffix. Existing source-branch databases with competing migration slots cannot be adopted by editing their journal; this port targets the integration base's history.

## Validation boundary

Pinned tools: .NET SDK 10.0.112, Node 22.23.3, npm 12.1.0. Locked npm installs and .NET restore succeeded. OpenAPI documents and Angular API types were regenerated from the combined implementation.

`dotnet format --verify-no-changes --no-restore` and the full Release solution build passed (zero build warnings/errors). The final targeted CSV/editor/proposal/CLI/contract run passed **301 tests**, skipped **33** environment-dependent tests, and had no failures. The skipped set includes the new populated-draft PostgreSQL upgrade test; compilation is not execution evidence for that test.

The full `npm run check` and production `npm run build` passed. The production build reports a 500 kB initial-bundle budget warning (655.60 kB combined bundle). Portable checks include CSV parser fixtures, contract/type checks, dependency-security checks, and the browser-session probe build; they do not run a real browser.

Linux native Rust/compiler execution, PostgreSQL installation/upgrade and restricted-role runtime tests, and real browser acceptance remain unqualified on this Windows lane. In particular, run the populated-draft upgrade test and retained draft-preview tests on PostgreSQL before release. Format execution support does not establish AI mapping-authoring capability.
