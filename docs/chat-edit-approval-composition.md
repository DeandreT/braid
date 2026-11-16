# Exact chat approval: production composition

The server composes `IChatMappingEditProposalStore` as `PostgresChatEditProposalStore` and registers `ChatMappingEditApprovalService`. `ChatHostDatabases` owns three independent lazy pools for the host lifetime and disposes every pool on shutdown, including when an earlier disposal fails:

- Reader: `braid_chat_reader`, authorized proposal read/list.
- Writer: `braid_chat_writer`, verified generation admission. The ordinary chat ledger ports and write journal still borrow only this pool.
- Approver: `braid_chat_approver`, atomic exact approval and journal preparation. It is held privately by the composed proposal store, not substituted for the model-facing writer.

Only the dedicated authenticated approval endpoint calls approval. The model tool calls `ProposeAsync` then proposal creation; neither listing, discovery nor generic recovery can approve. Mapping editing must be enabled with its backend, report and ingestion dependencies to offer generation.

## Required configuration

Disabled chat reads none of these settings and registers no proposal persistence. Enabled chat requires all three credentials, even if other mapping-editing dependencies are disabled. Local/Development delivery uses:

```text
BRAID_CHAT_READER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=braid_chat_reader;Password=<private-reader-password>
BRAID_CHAT_WRITER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=braid_chat_writer;Password=<private-writer-password>
BRAID_CHAT_APPROVER_CONNECTION_STRING=Host=<database-host>;Database=<database>;Username=braid_chat_approver;Password=<private-approver-password>
```

Session deployments use protected connection files:

```text
BRAID_CHAT_READER_CONNECTION_STRING_FILE=/run/braid/secrets/chat-reader-connection
BRAID_CHAT_WRITER_CONNECTION_STRING_FILE=/run/braid/secrets/chat-writer-connection
BRAID_CHAT_APPROVER_CONNECTION_STRING_FILE=/run/braid/secrets/chat-approver-connection
```

Each file contains its corresponding connection string and uses the existing protected-file rules. Plain environment connection strings are refused in Session mode, including dual plain/file delivery. No approver configuration is inferred from the writer, migrated from another role, or replaced with an in-memory store.

Startup validates exact usernames, nonempty independent passwords, the same host/port/database, and safe transaction/reset settings. Alternative passfile/client-certificate delivery is rejected. Failures are categorical and contain no connection string, password, parser diagnostics or inner exception. Connections stay lazy; each checkout checks PostgreSQL roles, schema and grants.

## Local role provisioning tooling

`deploy/local/.env.example` includes `BRAID_LOCAL_CHAT_APPROVER_PASSWORD`. Fresh `postgres-init.sh`, Compose environment delivery, and `dev-up.sh` credential discovery/server settings include the approver. All four chat-role passwords (including lifecycle) must be independent.

`postgres-add-chat-roles.sh` provisions all four chat roles with database CONNECT.
Deliver its environment privately. Schema installation supplies the exact
schema/table/function grants.

The role is LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOINHERIT, NOREPLICATION, NOBYPASSRLS, with no memberships. Do not grant the writer membership in it or share its credential with a model provider or worker.

The Compose database service supplies the approver variable to its mounted shared init script. Both Compose smoke/acceptance generators create an independent value; the feature override pairs `BRAID_CHAT_APPROVER_CONNECTION_STRING_FILE=/run/braid/secrets/chat-approver` with `BRAID_DELIVER_FILE_CHAT_APPROVER`. The generic entrypoint creates the private file and unsets the delivery variable before starting the server.
