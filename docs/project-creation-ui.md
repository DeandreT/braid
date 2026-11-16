# Create a project

The Projects page offers **Create project** when the current organization permits
creation and the server has enabled the feature. Project administration and
organization creation permission are separate: a creator does not need an
existing project membership, and being an administrator of another project does
not grant creation permission.

The dedicated creation page accepts a name and optional description. Names use
the server's whitespace trimming and Unicode scalar limits; descriptions retain
their exact text. Validation does not count supplementary Unicode characters as
two characters. Values are rendered as text.

## One request and deliberate recovery

Each deliberate Create action refreshes admission before sending. The first send
freezes one original organization, actor, request key and exact body in memory.
Editing and duplicate submission are disabled while that request is unresolved.
Opening the page, focusing the window or refreshing access never submits it.

An interrupted response does not prove that creation failed. **Recover creation**
deliberately resends the original request after checking fresh admission. Its key
and body remain unchanged. A later permission refusal or busy response cannot
resolve an earlier uncertain outcome. Starting another request after uncertainty
requires an explicit decision that acknowledges the possibility of a duplicate
project. Project names are not recovery identifiers.

Only one unresolved request is retained. A change of actor or organization hides
its private contents and prevents replay under another identity. Returning as the
original identity can recover it while this application tab remains alive.
Request keys, bodies and tokens are not stored in URLs or browser storage.

This recovery state survives normal application navigation and server/database
restart while the tab stays open. Reloading or closing the tab loses it; an
unresolved request activates the browser's leave warning. After that information
is lost, browsing the ordinary catalog cannot establish which request created a
similarly named project.

## Confirmation and current access

A validated creation or replay receipt confirms the original creation. Its
initial administrator membership describes history. A separate fresh project
read determines current access and actions, including later demotion or removal.

If that read fails, creation remains confirmed. **Retry access** performs reads
only. It never submits another creation request. Project metadata, mapping upload,
membership administration and report derivation remain separate workflows.

## Qualification

The browser scenario uses an actual packaged API and a restricted
project creator database identity, without native workers or private-object
storage. Controlled response cases supplement actual creation, replay and
permission checks. See the [fixture instructions](../tools/project-creation-browser/README.md).
