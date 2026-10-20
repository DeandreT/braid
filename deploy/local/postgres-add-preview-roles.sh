#!/usr/bin/env bash
set -euo pipefail

# Explicit administrator step for an existing local volume, before stop/migrate/start.
# Both CREATE ROLE statements and CONNECT grants are atomic; existing roles refuse.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
[[ "$POSTGRES_DB" == weft ]] || { echo 'This script targets only the local weft database.' >&2; exit 1; }
: "${WEFT_LOCAL_PREVIEW_READER_PASSWORD:?Set a separate preview reader password}"
: "${WEFT_LOCAL_PREVIEW_WRITER_PASSWORD:?Set a separate preview writer password}"
[[ "$WEFT_LOCAL_PREVIEW_READER_PASSWORD" != "$WEFT_LOCAL_PREVIEW_WRITER_PASSWORD" ]] || {
  echo 'Preview profile passwords must be independent.' >&2; exit 1;
}
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv preview_reader_password WEFT_LOCAL_PREVIEW_READER_PASSWORD
\getenv preview_writer_password WEFT_LOCAL_PREVIEW_WRITER_PASSWORD
BEGIN;
CREATE ROLE weft_preview_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_reader_password';
CREATE ROLE weft_preview_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_writer_password';
GRANT CONNECT ON DATABASE weft TO weft_preview_reader,weft_preview_writer;
COMMIT;
SQL
