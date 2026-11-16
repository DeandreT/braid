# Creation server composition

This page owns concrete creation activation and runtime admission. [Session host](session-host.md)
owns protected host setup; [creation runtime](mapping-creation-runtime.md) owns
authenticated operations; [exact review](creation-review-ui.md) owns approval UX.
The [foundation record](mapping-creation-application.md) retains the original
application contracts, not instructions to use its old unimplemented ports.

`CreationHostConfiguration` wires the authenticated server to the concrete creation store, discovery adapter, authoring compiler and authored-preview adapter. `WEFT_CREATION_ENABLED` defaults false; disabled composition reads only that flag and registers the disabled option. It opens no connection, credential file or service channel.

## Required composition

Enable creation in the session host with catalog/ingestion, chat and specification services configured. Use the existing pinned Ferrule service connector (mutual TLS or the separately admitted connector implementation); creation uses its exact configured build digest. The legacy inspection-only transport cannot supply this composition.

Deliver these three separate credentials through protected files:

- `WEFT_POSTGRES_CREATION_READER_CONNECTION_STRING_FILE`: `weft_creation_reader`
- `WEFT_POSTGRES_CREATION_WRITER_CONNECTION_STRING_FILE`: `weft_creation_writer`
- `WEFT_POSTGRES_CREATION_APPROVER_CONNECTION_STRING_FILE`: `weft_creation_approver`

The session host uses its existing protected-file validation. Plain environment connection values refuse startup. Roles and passwords must be distinct, connections must target the same database, and reset/multiplexing/enlistment substitutions are refused. Creation is also checked against the configured chat-reader and spec-reader database and credentials. No credential appears in configuration errors or documentation examples.

The specification host supplies its actual derived `SpecStoreBinding` as a singleton and its restricted database keyed by `SpecDatabaseProfile.Reader`. The binding must be the same value used by `ISpecBundleServiceFactory`; creation does not independently reconstruct a location digest. That shared binding registration is the document-host integration contract.

Registration validates credential delivery and installs lazy DI factories. DI owns the concrete creation store and the dedicated authoring/authored-preview runners; hosted shutdown drains runners before provider disposal. The existing inspection connector/certificate owner remains responsible for its shared resources.

## Durable admission versus execution admission

`CreationHostAdmission` checks the actual creation reader/writer/approver contracts, spec-reader contracts and chat reader/writer/approver contracts. The PostgreSQL verification helpers invoke the same exact role/schema admission used by operations, without inventing an actor or reading business content. The complete 0046 checks cover five-round chat storage, Specification audit kind 5 and closed creation-proposal citation/fingerprint storage. Verification is fresh for each runtime admission, bounded by 30 seconds, and fails closed. Every subsequent database operation retains its existing transaction-level guards and actor/project authorization.

Durable admission does not admit a physical source store or contact Ferrule. Thus retained GET/list and terminal receipt recovery remain available under current content permission even if source storage is inactive/relocated or workers are unavailable. Selection reads and fresh generation retain their own source-store and snapshot checks.

The host registers the real `FerruleMappingCreationCompiler` and `FerruleMappingCreationPreview` with dedicated `FerruleServiceRunner` instances that require the exact creation engine revision. On every actual execution channel, including each retry, the runner obtains readiness and uses `FerruleServiceCapabilities` to verify capabilities before sending any frame bytes on that same channel. It checks service protocol, replica continuity, exact build digest, engine revision and the required fixed profile's request/response, wall, CPU and memory limits. Authoring requires `authoring`; preview requires `authored-preview`. Missing, duplicate, reserved, malformed or mismatched profiles refuse execution. Responses are bounded to 16 KiB and readiness plus capability admission to ten seconds. A successful proof on an earlier channel never authorizes a later channel. The runner retains its fixed execution limits and quarantine/shutdown rules. Existing callers that do not request creation capability admission retain their readiness contract.

There is no environment qualification bit. Successful capability admission proves the responding pinned service advertises the exact required contract; installation/containment qualification is still provided by the deployed service's existing worker controls and qualification process.

## Local verification

`CreationHostConfigurationTests` covers default-off resource isolation, enabled non-session refusal, role/password/database/reset refusal, localhost pinned-mTLS capability admission, and actual native authoring/preview through the registered adapters. The native executable tests use synthetic input and do not establish Linux containment.

The real PostgreSQL 16.15 composition tests admit installed 0046, preserve durable admission with an inactive source store and stopped workers, then refuse after tampering with five-round, audit or citation contracts. Their physical-source/ingestion placeholders are unused: these tests qualify concrete database admission, while the separate native transport test qualifies compiler/preview composition.

### Historical composition checkpoint

The following Windows result and remaining gaps describe the original composition
run, not current setup status. Later bounded environment evidence is retained in
[v1 integration](v1-integration.md). Neither record establishes provider/cloud
acceptance or removes production activation gates.

Final pre-commit qualification on Windows with .NET SDK 10.0.112: the combined creation-host, Ferrule runner/recovery/compile, document-evidence and creation-discovery selection passed **85 tests, with 15 skips and zero failures** (100 total). Actual PostgreSQL 16.15 and native authoring, authored-preview and PDF executables were configured. The PostgreSQL work root was unique to this run, and reviewed source hashes were unchanged throughout execution.

- Creation host: 27 passed, one Linux protected-file case skipped.
- Ferrule runner: 21 passed; recovery: 15 passed.
- Ferrule compile: two passed, 14 Linux live-compile/equivalence cases skipped.
- Document-evidence persistence: 18 passed; creation discovery: two passed.

The host cases include eight split-replica cases covering both creation profiles, incompatible profile limits or engine revisions, and direct or post-busy retry channels; six altered/malformed capability cases; actual native authoring/preview execution; and real PostgreSQL admission. Every incompatible split-replica channel received zero operation requests, and each opened channel was independently capability-checked. Protected Linux file/session startup, Linux live compilation/equivalence, deployment and live provider/Azure access remain separate qualification lanes.
