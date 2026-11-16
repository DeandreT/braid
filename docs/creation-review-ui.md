# Exact creation review

This page owns the exact-proposal review UI contract. [Creation runtime](mapping-creation-runtime.md)
owns authenticated operations, [proposal library](creation-proposal-library.md)
owns discovery and [creation host composition](creation-host-composition.md) owns
configuration.

The lazy-loaded review route is
`/catalog/organizations/{organization}/projects/{project}/creation/{proposal}?fingerprint={fingerprint}`.
It reads the authenticated creation endpoint using the expected fingerprint.
Missing or malformed identity fields refuse admission. The server's runtime
capability and durable proposal authority remain the admission boundary.

The page shows the retained plan, source citations, assumptions and questions.
Plan text is checked against its exact UTF-8 SHA-256 digest before display.
Critical questions block approval; each critical assumption requires its own
acknowledgement, plus an explicit review of the exact plan. Approval submits only
that fingerprint and the sorted critical-assumption IDs. The UI preserves all
immutable review facts across reads, saves and recovery responses.

Samples are bounded to 1 MiB before sending and hashed over their exact bytes.
Preview output must match the proposal, native and sample digests, its declared
format/MIME pair and its actual output digest. The output is displayed as text.
Successful empty output and failed execution have separate representations.

An uncertain save disables further approval until explicit recovery reconciles
the original operation. Refreshing alone does not convert an uncertain response
into permission to send another approval. The recovery button calls only the
recovery endpoint. A reconciled retained approval with no reservation, or a
Reserved operation needing its source, offers a deliberate retry of that same
proposal after renewed review and critical-assumption acknowledgements.
Confirmed committed receipts link to the saved revision; later observations
must preserve operation identity and terminal mapping/revision identities.

Identity replacement or access denial clears source facts, sample data and review
acknowledgements. Async responses are bound to the route generation and admitted
owner lineage. In-tab samples use the existing unsent-work navigation guard.
An owner/selection-scoped digest of immutable review facts survives failed reads
so a second refresh cannot replace previously observed facts under one fingerprint.

`apps/web/playwright.creation-review.config.ts` runs strict parser and actual
Chromium fixtures for exact approval, recovery, preview substitution, access
revocation, owner replacement, inert rendering and accessibility.
