# Creation proposal discovery in chat

Workspace chat accepts Specification disclosure events through the tool's 12,288-byte
budget, with a null version and exact project/extraction IDs. Existing disclosure
sources retain their 8,192-byte limit.

Trusted `creationProposal` citations carry the organization, project, proposal ID
and lowercase SHA-256 fingerprint. Unrelated mapping, revision, report and version
fields must be null. The link opens the exact creation review page. Both browser
and server return-path validation retain that fingerprint across sign-in.

The review page uses [exact creation review](creation-review-ui.md). Discovery,
generation and document-sharing checks are described in the
[creation runtime](mapping-creation-runtime.md).
