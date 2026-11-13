# Exact chat approval: production composition

The server now composes `IChatMappingEditProposalStore` as `PostgresChatEditProposalStore` and registers `ChatMappingEditApprovalService`. `ChatHostDatabases` owns three independent lazy pools for the host lifetime and disposes every pool on shutdown, including when an earlier disposal fails:

- Reader: `weft_chat_reader`, authorized proposal read/list.
- Writer: `weft_chat_writer`, verified generation admission. The ordinary chat ledger ports and write journal still borrow only this pool.
- Approver: `weft_chat_approver`, atomic exact approval and journal preparation. It is held privately by the composed proposal store, not substituted for the model-facing writer.

Only the dedicated authenticated approval endpoint calls approval. The model tool calls `ProposeAsync` then proposal creation; neither listing, discovery nor generic recovery can approve. The existing mapping-editing feature must also be enabled, with its verified backend, report and ingestion dependencies, to offer generation. There is no direct-save fallback when proposal services are unavailable. Keep `WEFT_CHAT_LEGACY_DIRECT_SAVE_ENABLED` absent/false for the exact-approval deployment; migration 0043 also revokes the old writer preparation capability.

## Required configuration

Disabled chat reads none of these settings and registers no proposal persistence. Enabled chat requires all three credentials, even if other mapping-editing dependencies are disabled. Local/Development delivery uses:

```text
WEFT_CHAT_READER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=weft_chat_reader;Password=<private-reader-password>
WEFT_CHAT_WRITER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=weft_chat_writer;Password=<private-writer-password>
WEFT_CHAT_APPROVER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=weft_chat_approver;Password=<private-approver-password>
```

These are placeholders, not usable credentials. Session deployments must instead provide protected connection files:

```text
WEFT_CHAT_READER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-reader-connection
WEFT_CHAT_WRITER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-writer-connection
WEFT_CHAT_APPROVER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-approver-connection
```

Each file contains its corresponding connection string and uses the existing protected-file rules. Plain environment connection strings are refused in Session mode, including dual plain/file delivery. No approver configuration is inferred from the writer, migrated from another role, or replaced with an in-memory store.

Startup validates exact usernames, nonempty independent passwords, the same host/port/database, and safe transaction/reset settings. Alternative passfile/client-certificate delivery is rejected. Failures are categorical and contain no connection string, password, parser diagnostics or inner exception. Connections stay lazy; these startup checks do **not** replace per-checkout PostgreSQL role/schema/grant admission or establish that a database has been qualified.

## Local role provisioning tooling

`deploy/local/.env.example` includes the new placeholder `WEFT_LOCAL_CHAT_APPROVER_PASSWORD`. Fresh `postgres-init.sh`, Compose environment delivery, and `dev-up.sh` credential discovery/server settings now include the approver. All four chat-role passwords (including lifecycle) must be independent.

For an existing database that already has reader/writer/lifecycle roles, an administrator invokes `deploy/local/postgres-add-chat-approver-role.sh` with Bash explicitly before the coordinated stop/migrate/start cutover. Deliver its environment privately, following the same procedure as the existing chat-role script. It creates only `weft_chat_approver`, atomically grants database CONNECT, and refuses an existing role rather than adopting/resetting it. For a database with none of the chat roles, `postgres-add-chat-roles.sh` creates all four. Neither script grants schema/table/function access: migration 0043 owns the exact schema grants.

The role is LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOINHERIT, NOREPLICATION, NOBYPASSRLS, with no memberships. Do not grant the writer membership in it or share its credential with a model provider or worker. `dev-up.sh` does not auto-upgrade roles in existing volumes; run the explicit administrator upgrade first. This work changes tooling only; no role, resource or credential has been provisioned here. Azure secret/resource rollout remains separate deployment work.

The shipped Compose database service now supplies the required approver variable to its mounted shared init script. Both Compose smoke/acceptance generators create an independent value; the feature override pairs `WEFT_CHAT_APPROVER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-approver` with `WEFT_DELIVER_FILE_CHAT_APPROVER`. The existing generic entrypoint creates the private file and unsets the delivery variable before starting the server; no plaintext connection-string setting is added to the Session host.

The propagation audit also covers both browser init-script consumers (`tools/browser-fixture/runtime.mjs` and `tools/preview-browser/run.mjs`), local Compose, and `dev-up.sh`'s dynamically discovered role inventory. Their required password maps now include the approver. The smoke generator's missing COMPARISON value and the native provisioning test's missing draft-role values were corrected as part of the all-required-credentials audit. Administration stays explicitly optional through the init script's existing empty default. Azure provisioning/Bicep/secret delivery are separate and have not been changed or qualified.

The Azure templates still need their corresponding approver role and protected-delivery rollout before using this enabled-chat server composition; no Azure startup or full-migration qualification is implied by the portable inventory checks.

## Integration gates and checks

Runtime `7d0dc03c` was merged onto reviewed persistence `849b4f80` as local merge `0f115948`. Runtime negative fake-store tests and verified-generation tests are both retained. The application interface contains one bounded `ListAsync` method.

Actual CSV/XML migrations 0041/0042 are absent from this branch. Integrate them ahead of 0043; the consecutive migration runner is unchanged and refuses the incomplete inventory. No SQL migration, UI or XML files are changed by this composition slice. Quiesce legacy execution/recovery for the database approval gate, then qualify the combined deployment against PostgreSQL, including exact role grants, receipt-race regression and erasure. Portable DI tests do not prove database behavior.

Portable tests exercise the production registration helper used by the server: strict missing/conflicting configuration, real store/service resolution, verified-admission refusal, writer inability to approve, distinct lazy data sources, host-owned disposal and disabled behavior. They use no Unix file APIs and open no connections. Existing Linux protected-file/session-host tests are updated with the approver delivery and remain separate platform/runtime qualification gates.

Verified in this Windows worktree using pinned SDK 10.0.112 and locked restore with `%LOCALAPPDATA%/Weft/WindowsLane/cache/nuget`: **17 composition tests passed**. The broader focused run passed **148 tests** and failed the existing `ChatDatabaseConfigurationTests.ExactPinnedSourceHasSeparateReadOnlyAndWriterColumnMatricesWithoutSessionOrPayloadRights` inventory test because 0041/0042 are missing. That assertion and the migration runner remain unchanged. Pinned `dotnet format whitespace --verify-no-changes` passed for the six changed/new C# files; Bash syntax checks passed separately for all four changed/new scripts. No PostgreSQL, protected Unix-file runtime, hosted deployment or actual provisioning qualification is claimed. Implementation changes remain uncommitted for review; only the authorized runtime merge was committed.

Propagation-review verification: the complete `RegressionDeploymentProvisioningTests` plus portable composition suite passed **30**, with **1 native PostgreSQL theory skipped**. This includes the provisioning password inventory and consumer discovery checks; none were excluded for the missing predecessor migrations. Tests parse the actual Compose/generator declarations, compare them with all mandatory init-script credentials, and run the Compose-delivered synthetic connection values through production host admission. The browser fixture now explicitly inventories the real reviewed 0043 file and its three protected tables; no 0041/0042 placeholders or consecutive-migration exceptions were added. Its four portable schema/conservation tests pass. Database/container startup remains unqualified and still needs the real predecessor migrations.

Pinned C# whitespace verification, Prettier checks on the changed Compose/JavaScript files, Bash syntax checks on all seven audited scripts, JavaScript syntax checks, and `git diff --check` passed. No SQL migration files changed in the propagation fix.
