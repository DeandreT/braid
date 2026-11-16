#!/usr/bin/env bash
set -euo pipefail

# Explicit administrator upgrade step for a volume already containing the three chat roles.
# Never reset/adopt an existing approver role. Migration 0043 owns all schema/function grants.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
[[ "$POSTGRES_DB" == braid ]] || { echo 'This script targets only the local braid database.' >&2; exit 1; }
: "${BRAID_LOCAL_CHAT_APPROVER_PASSWORD:?Set a separate chat approver password}"
: "${BRAID_LOCAL_CHAT_READER_PASSWORD:?Set the existing chat reader password}"
: "${BRAID_LOCAL_CHAT_WRITER_PASSWORD:?Set the existing chat writer password}"
: "${BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD:?Set the existing chat lifecycle password}"
for other in "$BRAID_LOCAL_CHAT_READER_PASSWORD" "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD"; do
  [[ "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$other" ]] || { echo 'Chat approver credentials must be independent.' >&2; exit 1; }
done
unset other
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv chat_approver_password BRAID_LOCAL_CHAT_APPROVER_PASSWORD
BEGIN;
CREATE ROLE braid_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_approver_password';
GRANT CONNECT ON DATABASE braid TO braid_chat_approver;
COMMIT;
SQL
