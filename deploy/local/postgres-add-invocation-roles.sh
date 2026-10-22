#!/usr/bin/env bash
set -euo pipefail
: "${WEFT_LOCAL_INVOCATION_GRANT_WRITER_PASSWORD:?Set an independent invocation grant writer password}"
: "${WEFT_LOCAL_INVOCATION_WRITER_PASSWORD:?Set an independent invocation writer password}"
[[ "$WEFT_LOCAL_INVOCATION_GRANT_WRITER_PASSWORD" != "$WEFT_LOCAL_INVOCATION_WRITER_PASSWORD" ]] || {
  echo 'Invocation credentials must be independent.' >&2; exit 1;
}
# Explicit administrator upgrade step. No password rotation or existing-role adoption.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv grant_writer_password WEFT_LOCAL_INVOCATION_GRANT_WRITER_PASSWORD
\getenv invocation_writer_password WEFT_LOCAL_INVOCATION_WRITER_PASSWORD
BEGIN;
CREATE ROLE weft_invocation_grant_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'grant_writer_password';
CREATE ROLE weft_invocation_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'invocation_writer_password';
GRANT CONNECT ON DATABASE weft TO weft_invocation_grant_writer,weft_invocation_writer;
COMMIT;
SQL
