# Catalog browser

This read-only increment connects Angular to the existing catalog API. Its scope
is [#75](https://github.com/DeandreT/weft/issues/75), the first part of
[#19](https://github.com/DeandreT/weft/issues/19). It merged through
[PR #76](https://github.com/DeandreT/weft/pull/76) after all 20 catalog and 22
existing example/shell browser cases passed. [Saved mapping reports](saved-reports.md)
follow in increment B. Uploads, metadata editing, sign-in and chat retain their
separate increments.

## Navigation

Examples remain at `/examples`; `/` redirects there. Catalog opens the current
Development workspace's admitted organization and visible projects. Project
pages list mappings; mapping pages show current metadata and revision history.
Each revision has a concrete URL retaining its organization, project, mapping
and revision IDs. Opening a saved revision never follows later head changes.
`View current head` reads the mapping afresh and opens its returned concrete
revision.

Lists show up to ten entries in server order. Next page uses the server's opaque
cursor; First page removes it. Browser Back/Forward reads the selected page
afresh. There is no inferred total count or client-side reordering. Versions
and revision sequences remain exact strings.

Revision details show sequence, admission, digest, byte count, original filename,
logical document and commit time. A decoded-invalid revision remains part of
history. Mapping names and descriptions alongside an old revision are labelled
as current metadata. Catalog metadata reads never derive explanations. When
report reading is enabled, the revision page reads saved reports separately.

## Access and refresh

The server supplies the Development identity and checks access on every read.
The browser offers no actor or role selector. Route changes, explicit refresh
and returning to a focused/visible document obtain fresh context and data.
Old content clears while a new selection loads; canceled or late reads cannot
replace the current selection. There is no periodic polling or instantaneous
revocation notification while a tab makes no requests.

Accessible empty lists, unavailable/not-found resources, rejected workspace
identity, invalid links and operational failures have distinct states. A role
name is descriptive; the server remains authoritative. Context and request
tokens stay in memory and are not written to browser storage.

Use the built Angular application at the API's exact configured loopback origin
with the explicit catalog reader and Development identity configuration in
[Catalog reads](catalog-reads.md). The separate Angular development proxy retains
its existing Host behavior; catalog security settings are not relaxed for it.

## Verification

The existing example/shell browser configuration retains its 22 cases. The
separate `npm run test:catalog-browser` command uses the packaged API origin and
a disposable actual PostgreSQL/native-ingestion fixture. Both commands allocate
an alternate virtual display. Catalog traces are disabled so framework request
tokens are not retained in network recordings.

The catalog fixture setup and its private configuration are separate from
application runtime configuration. Real product HTTP creates the project,
mapping and revision fixtures. Read checks then run with writes, reports, worker
and private-root configuration disabled. Synthetic response cases cover UI
cancellation, incompatible scopes and exact large-number rendering separately
from persistence checks. The executable operator and invocation details are
recorded with this increment's final verification instructions.
