# Proposal library and feedback

The project page shows the proposal-library entry only after the authenticated
`creation-capabilities` response reports `available: true`. The library checks
current identity and project access again before each read. It uses the server's
closed `CreationProposalPageV1` shape (`proposals`, `afterProposal`), requests 20
items at a time, and renders at most 200 per selection. Refresh starts a new
scan. Each returned review link must match its organization, project, proposal
and lowercase fingerprint exactly. Duplicate/backward pages and changed
fingerprints for previously observed immutable proposals are refused.

Opening, refreshing or paging the library never approves a mapping. Approval
continues through the existing exact-fingerprint review page, including current
content permission, required acknowledgements and its durable receipt.

## Feedback handoff

The review page's feedback link carries only `creationProjectId`,
`creationProposalId` and `creationFingerprint`. Neither the sample nor its output
is carried into chat. The feedback page requires a separate **Prepare feedback
question** action. That action fills an editable question; the user adds the
requested changes and explicitly sends it. Preparing may create an empty
workspace conversation, but creates no model turn. Replacing an existing unsent
question requires confirmation. Declining keeps the draft.

Feedback uses a workspace conversation, not revision-pinned legacy chat.
Selecting legacy chat drops the creation-query family so the URL remains valid.
Both browser and server return-path validators accept the complete creation
identity tuple, optionally with a workspace conversation ID, and refuse mixed
legacy or incomplete tuples.

Pending preparation is fenced by the proposal selection, page lifetime, current
identity lineage, panel, requested conversation and draft. An obsolete async
completion cannot insert its question into a newer selection or overwrite newer
work. The question asks the provider to read the exact retained proposal through
`read_specification mode=proposal`, recheck current selected evidence, and create
a new immutable proposal. Each replacement still requires its own exact review
and approval. Tool authorization, model-sharing grants, bounded retained context
and auditing are enforced by the separate feedback API implementation.

## Qualification

`playwright.creation-library.config.ts` runs the closed-page checks and actual
Chromium library, capability and feedback navigation tests using synthetic
fixtures. Browser transport fixtures are not live-provider acceptance. The
shared `contracts/browser-return-paths.json` vectors exercise the paired browser
and server return-path rules.
