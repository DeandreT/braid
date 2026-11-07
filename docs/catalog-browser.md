# Catalog browser

The Angular catalog opens the user's admitted organization and visible projects.
Saved revisions open in the workbench, with [saved mapping reports](saved-reports.md),
uploads, metadata editing and chat governed by their own feature and access checks.

## Navigation

The application root opens `/catalog`. Session deployments first show sign-in
when needed and return to the requested application path. Development opens the
configured workspace directly. Project
pages list mappings; mapping pages show current metadata and revision history.
Opening `/sign-in` while already signed in opens the catalog, regardless of a
return-path query. The header account menu reads the admitted organization on a
direct Help visit too; a failed organization read does not hide the user guide.
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

The server supplies the Development identity or verifies the signed-in session,
and checks access on every read.
The browser offers no actor or role selector. Route changes, explicit refresh
and returning to a focused/visible document obtain fresh context and data.
These routine access checks keep the workspace on screen. A new selection starts
empty and shows loading on its page; reading the displayed selection again keeps
it readable, with the page marked busy, until the fresh read settles.
Canceled or late reads cannot replace the current selection. Only a changed
identity, an ended session or a failed check hides and clears the workspace; see
[Session-safe browser state](browser-session-state.md). There is no periodic
polling or instantaneous revocation notification while a tab makes no requests.

Accessible empty lists, unavailable/not-found resources, rejected workspace
identity, invalid links and operational failures have distinct states. A role
name is descriptive; the server remains authoritative. Context and request
tokens stay in memory and are not written to browser storage.

Use the built Angular application at the API's exact configured loopback origin
with the explicit catalog reader and Development identity configuration in
[Catalog reads](catalog-reads.md). The separate Angular development proxy retains
its existing Host behavior; catalog security settings are not relaxed for it.

## Verification

The shell browser configuration covers entry and account navigation. The
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
