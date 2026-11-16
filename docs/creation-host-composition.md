# Creation server composition

This page describes creation configuration and runtime admission. [Session host](session-host.md)
owns protected host setup; [creation runtime](mapping-creation-runtime.md) owns
authenticated operations; [exact review](creation-review-ui.md) owns approval UX.
See [application contracts](mapping-creation-application.md) for the creation workflow.

`CreationHostConfiguration` wires the authenticated server to the concrete creation store, discovery adapter, authoring compiler and authored-preview adapter. `BRAID_CREATION_ENABLED` defaults false; disabled composition reads only that flag and registers the disabled option. It opens no connection, credential file or service channel.

## Required composition

Enable creation in the session host with catalog/ingestion, chat and specification services configured. Use the pinned Ferrule service connector (mutual TLS or the separately admitted connector implementation); creation uses its exact configured build digest.

Deliver these three separate credentials through protected files:

- `BRAID_POSTGRES_CREATION_READER_CONNECTION_STRING_FILE`: `braid_creation_reader`
- `BRAID_POSTGRES_CREATION_WRITER_CONNECTION_STRING_FILE`: `braid_creation_writer`
- `BRAID_POSTGRES_CREATION_APPROVER_CONNECTION_STRING_FILE`: `braid_creation_approver`

The session host uses its existing protected-file validation. Plain environment connection values refuse startup. Roles and passwords must be distinct, connections must target the same database, and reset/multiplexing/enlistment substitutions are refused. Creation is also checked against the configured chat-reader and spec-reader database and credentials. No credential appears in configuration errors or documentation examples.

The specification host supplies its actual derived `SpecStoreBinding` as a singleton and its restricted database keyed by `SpecDatabaseProfile.Reader`. The binding must be the same value used by `ISpecBundleServiceFactory`; creation does not independently reconstruct a location digest. That shared binding registration is the document-host integration contract.

Registration validates credential delivery and installs lazy DI factories. DI owns the concrete creation store and the dedicated authoring/authored-preview runners; hosted shutdown drains runners before provider disposal. The existing inspection connector/certificate owner remains responsible for its shared resources.

## Durable admission versus execution admission

`CreationHostAdmission` checks the creation reader/writer/approver contracts, spec-reader contracts and chat reader/writer/approver contracts. The PostgreSQL verification helpers invoke the same exact role/schema admission used by operations without reading business content. Checks cover five-round chat storage, Specification audit kind 5 and closed creation-proposal citation/fingerprint storage. Verification is fresh for each runtime admission, bounded by 30 seconds, and fails closed. Every subsequent database operation retains its transaction-level guards and actor/project authorization.

Durable admission does not admit a physical source store or contact Ferrule. Thus retained GET/list and terminal receipt recovery remain available under current content permission even if source storage is inactive/relocated or workers are unavailable. Selection reads and fresh generation retain their own source-store and snapshot checks.

The host registers the real `FerruleMappingCreationCompiler` and `FerruleMappingCreationPreview` with dedicated `FerruleServiceRunner` instances that require the exact creation engine revision. On every actual execution channel, including each retry, the runner obtains readiness and uses `FerruleServiceCapabilities` to verify capabilities before sending any frame bytes on that same channel. It checks service protocol, replica continuity, exact build digest, engine revision and the required fixed profile's request/response, wall, CPU and memory limits. Authoring requires `authoring`; preview requires `authored-preview`. Missing, duplicate, reserved, malformed or mismatched profiles refuse execution. Responses are bounded to 16 KiB and readiness plus capability admission to ten seconds. A successful proof on an earlier channel never authorizes a later channel. The runner retains its fixed execution limits and quarantine/shutdown rules. Existing callers that do not request creation capability admission retain their readiness contract.

## Local verification

`CreationHostConfigurationTests` covers default-off resource isolation, enabled non-session refusal, role/password/database/reset refusal, localhost pinned-mTLS capability admission, and actual native authoring/preview through the registered adapters.

PostgreSQL 16.15 composition tests preserve durable admission with an inactive
source store and stopped workers, then refuse after tampering with five-round,
audit or citation contracts. Native transport tests exercise compiler/preview
composition.
