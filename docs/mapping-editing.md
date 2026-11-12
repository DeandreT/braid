# Mapping editing

The workbench edits Ferrule mappings through `ferrule.edit.v1` operations. Ferrule's
headless `edit-ops` library owns the edits and command-based undo history. The same
core runs in a browser worker and in the authoritative server worker. Neither
Angular, the CLI nor C# decodes, patches or serializes native project JSON.

## Drafts and authority

`WEFT_MAPPING_EDITING_ENABLED` is off by default. Enabling it requires catalog
reads, revision ingestion, report derivation, an edit-capable Ferrule runner and
separate `WEFT_POSTGRES_DRAFT_READER_CONNECTION_STRING` and
`WEFT_POSTGRES_DRAFT_WRITER_CONNECTION_STRING` credentials. Session mode uses
their protected `_FILE` delivery counterparts. Provision `weft_draft_reader`
and `weft_draft_writer` as CONNECT-only logins before applying the ordered
draft migrations. They receive function execution, not direct table access.

Publishers and administrators have `EditMapping`. A draft belongs to one author
and one mapping; another author receives 404 even if they are an administrator.
Every operation re-checks current authority. Demotion hides the draft without
giving it to another member.

Opening edit mode resumes that author's open draft. The base is an immutable
revision ID, sequence and digest. Creating a draft runs a zero-operation
checkpoint, refusing bases whose re-encoding exceeds 4 MiB. The operation log is
at most 2,000 operations and 1 MiB canonical UTF-8 JSON. Its version is represented
by a strong ETag. Autosave replaces the complete effective log with `If-Match`,
after two seconds idle or twenty edits. It does not run a server worker.

Open drafts expire thirty days after their last change. Discard and expiry remove
operation and checkpoint content. They do not remove saved revisions or previews.

## Operations and history

The format uses decimal strings for IDs and structural positions, and typed text
for scalar values. Rust parses integers and floating-point values, including
integers beyond JavaScript's exact range and negative zero. Unknown members are
refused. A creating operation carries an unused node ID so replay is deterministic.

Supported operations bind, move or remove field bindings; connect or disconnect
inputs; edit constants, builtin calls and value maps; remove nodes with orphan
cleanup; add or remove child scopes; set or clear source iteration, filters,
sorting and grouping; and add, remove, move or edit failure rules. The inspector
offers field mapping controls and a typed-parameter editor for the full operation
vocabulary. Schemas, named boundaries, user functions, joins, filter/map sequences,
XML types, dynamic fields and format options remain read-only and are preserved.

Undo and redo use Ferrule commands rather than retained whole-project snapshots.
Only the effective operation list is autosaved. The redo branch remains private
tab memory. A session boundary terminates the worker and hides its view; the same
owner may restore retained work after fresh admission. A different admitted owner
clears that memory. No native project or operation log is stored in browser storage.

## Checkpoint, comparison, preview and save

A checkpoint replays the stored operations on the exact base in the sandboxed
worker, encodes through Ferrule, validates and projects. Local browser validation
is provisional; checkpoint validation is authoritative. There is one retained
checkpoint for the current draft version. Updating operations invalidates it.

Comparison uses the base's retained reports and the checkpoint's semantic facts,
with a draft/version citation and a readable operation list. It is not a byte
diff: Ferrule may normalize defaults and envelopes without changing the model.

A draft preview copies the checkpoint native bytes into the preview's own
documents. Its subject cites draft, version and base revision. The run remains
replayable after edits or discard and expires with ordinary preview retention
(24 hours by default). Draft previews require the author's edit authority,
including at execution and subsequent reads.

Save requires nonempty operations and a current checkpoint. It submits only the
server-generated native bytes through revision ingestion with `head=equal` to
the draft's base, original actor and an idempotency key. Ingestion independently
validates and re-checks authority. A moved head produces a terminal conflict and
no revision. Rebase is explicit and all-or-nothing; a rejected operation reports
its index. Discard is the other way to abandon a conflicting draft.

Saving freezes the base, log, checkpoint, key and filename. Recovery observes the
immutable ingestion receipt and writes the revision's edit provenance idempotently,
then deletes checkpoint bytes. A crash between ingestion and reconciliation does
not lose the operation log or create another revision on retry.

## HTTP and CLI

Under `/api/v1/organizations/{org}/projects/{project}/mappings/{mapping}/drafts`:

| Method and suffix | Purpose |
| --- | --- |
| `POST`, `GET` | Create with `baseRevisionId` and idempotency key; list own open drafts |
| `GET /{draft}` | Metadata and ETag |
| `GET /{draft}/base` | Verified exact immutable native bytes |
| `GET`, `PUT /{draft}/operations` | Read or conditionally replace the closed operation log |
| `POST /{draft}/checkpoints` | Checkpoint a named version |
| `GET /{draft}/checkpoints/current/{validation,projection}` | Current authoritative reports |
| `GET /{draft}/comparison` | Semantic base/checkpoint comparison |
| `POST /{draft}/previews` | Submit an independently retained preview |
| `POST /{draft}/save` | Conditional revision ingestion |
| `POST /{draft}/rebase` | Explicit current-head rebase with `If-Match` |
| `DELETE /{draft}` | Conditional discard |

All responses use `no-store`; unsafe methods use normal catalog origin/request-token
admission. Checkpoint, rebase and save share a two-permit local gate and return 503
when busy. Worker timeout, isolation and protocol failures remain distinct.

The CLI exposes `drafts create|list|show|discard`, `drafts ops get|put`, and
`drafts checkpoint|compare|preview|save|rebase`. Commands take `--project` and
`--mapping`; creation takes `--base-revision`. Use `--request-id` for recoverable
create/save intent and `--if-match` to supply a previously reviewed ETag.

## Browser isolation and packaging

The application shell CSP has `script-src 'self'`, without `wasm-unsafe-eval`.
The shell uses root-absolute asset URLs and an explicit Angular router base,
not an HTML base element, because `base-uri 'none'` also applies to deep links.
The unshipped Vite development-server index retains a base element: Angular's
development server deliberately ignores `deployUrl`. Packaged builds use the
CSP-compatible index in both production and development build configurations.
Critical-CSS inlining is disabled for the packaged shell so loading styles does
not rely on an inline `onload` handler that the script policy would refuse.
The dedicated `/edit/` module worker alone permits wasm compilation; its policy
allows scripts only under the configured origin's `/edit/` path and forbids
network APIs and nested workers. Angular fetches the module with the SHA-256
integrity embedded in its build, then transfers its bytes to the worker. Calls
have a ten-second watchdog. The production module has no native-byte export.

`engines/ferrule-edit-wasm/Dockerfile` uses the native worker's pinned Rust
toolchain and wasm-bindgen 0.2.129. `tools/edit-core/package-assets.py` checks the
wasm import allowlist, refuses qualification exports, copies the `/edit/` assets
and generates the Angular integrity manifest. Build and package the module before
the Angular build. Generated binaries stay outside Git. The separate qualification
module exposes checkpoint digests only for native/Node/Chromium equivalence tests.

The live acceptance harness reuses the owned preview fixture with `--editing`:
`node tools/preview-browser/run.mjs --editing --publications <published-hosts> --worker-image sha256:<qualified-image>`.
Run it under the environment's isolated virtual display and browser-resource lock.
It owns and removes its PostgreSQL container, network, volumes, private storage and
host processes; it never connects to an existing application database.
