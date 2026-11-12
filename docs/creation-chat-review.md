# Creation proposal discovery in chat

Workspace chat accepts Specification disclosure events through the tool's 12,288-byte
budget, with a null version and exact project/extraction IDs. Existing disclosure
sources retain their 8,192-byte limit.

Trusted `creationProposal` citations carry the organization, project, proposal ID
and lowercase SHA-256 fingerprint. Unrelated mapping, revision, report and version
fields must be null. The link opens the exact creation review page. Both browser
and server return-path validation retain that fingerprint across sign-in.

The local citation type extends the generated type until the corresponding runtime
contract is integrated. Server generation, durable discovery and document-sharing
authorization are supplied by the separately reviewed runtime/storage units.

## Verification

- 14 chat/parser/return-path tests passed.
- Browser TypeScript and Angular development build passed.
- 153 .NET return-path cases passed; two Linux-only flow cases were skipped.
- One actual Chromium journey passed with a synthetic authenticated HTTP fixture:
  a 9,000-byte Specification disclosure completes, its creation citation opens the
  exact proposal GET with the session cookie, approval remains disabled pending
  review, and retained events replay after reload without another turn request.
  The fixture verifies that navigation sends neither approval nor recovery requests.

The browser fixture qualifies event handling and navigation. Live provider,
PostgreSQL-backed discovery and Linux session execution require their integration
qualification.
