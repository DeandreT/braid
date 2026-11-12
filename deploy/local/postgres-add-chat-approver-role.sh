#!/usr/bin/env bash
set -euo pipefail

# Explicit administrator upgrade step for a volume already containing the three chat roles.
# Never reset/adopt an existing approver role. Migration 0043 owns all schema/function grants.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
[[ "$POSTGRES_DB" == weft ]] || { echo 'This script targets only the local weft database.' >&2; exit 1; }
: "${WEFT_LOCAL_CHAT_APPROVER_PASSWORD:?Set a separate chat approver password}"
: "${WEFT_LOCAL_CHAT_READER_PASSWORD:?Set the existing chat reader password}"
: "${WEFT_LOCAL_CHAT_WRITER_PASSWORD:?Set the existing chat writer password}"
: "${WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD:?Set the existing chat lifecycle password}"
for other in "$WEFT_LOCAL_CHAT_READER_PASSWORD" "$WEFT_LOCAL_CHAT_WRITER_PASSWORD" "$WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD"; do
  [[ "$WEFT_LOCAL_CHAT_APPROVER_PASSWORD" != "$other" ]] || { echo 'Chat approver credentials must be independent.' >&2; exit 1; }
done
unset other
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv chat_approver_password WEFT_LOCAL_CHAT_APPROVER_PASSWORD
BEGIN;
CREATE ROLE weft_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_approver_password';
GRANT CONNECT ON DATABASE weft TO weft_chat_approver;
COMMIT;
SQL
