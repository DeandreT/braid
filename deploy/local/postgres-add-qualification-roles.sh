#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_QUALIFICATION_READER_PASSWORD:?Set an independent qualification reader password}"
: "${BRAID_LOCAL_QUALIFICATION_WRITER_PASSWORD:?Set an independent qualification writer password}"
[[ "$BRAID_LOCAL_QUALIFICATION_READER_PASSWORD" != "$BRAID_LOCAL_QUALIFICATION_WRITER_PASSWORD" ]] || {
  echo 'Qualification credentials must be independent.' >&2; exit 1;
}
# Explicit administrator upgrade step. No password rotation, existing-role adoption or application invocation.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv reader_password BRAID_LOCAL_QUALIFICATION_READER_PASSWORD
\getenv writer_password BRAID_LOCAL_QUALIFICATION_WRITER_PASSWORD
BEGIN;
CREATE ROLE braid_qualification_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'reader_password';
CREATE ROLE braid_qualification_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'writer_password';
GRANT CONNECT ON DATABASE braid TO braid_qualification_reader,braid_qualification_writer;
COMMIT;
SQL
