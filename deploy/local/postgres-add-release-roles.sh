#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_RELEASE_READER_PASSWORD:?Set an independent release reader password}"
: "${BRAID_LOCAL_RELEASE_WRITER_PASSWORD:?Set an independent release writer password}"
[[ "$BRAID_LOCAL_RELEASE_READER_PASSWORD" != "$BRAID_LOCAL_RELEASE_WRITER_PASSWORD" ]] || {
  echo 'Release credentials must be independent.' >&2; exit 1;
}
# Explicit administrator upgrade step. No password rotation or existing-role adoption.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv reader_password BRAID_LOCAL_RELEASE_READER_PASSWORD
\getenv writer_password BRAID_LOCAL_RELEASE_WRITER_PASSWORD
BEGIN;
CREATE ROLE braid_release_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'reader_password';
CREATE ROLE braid_release_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'writer_password';
GRANT CONNECT ON DATABASE braid TO braid_release_reader,braid_release_writer;
COMMIT;
SQL
