#!/usr/bin/env bash
# Explicit administrator upgrade before migration 0018. Does not rotate or adopt existing roles.
set -euo pipefail
: "${WEFT_LOCAL_BUILD_READER_PASSWORD:?Set a separate build reader password}"
: "${WEFT_LOCAL_BUILD_WRITER_PASSWORD:?Set a separate build writer password}"
[[ "$WEFT_LOCAL_BUILD_READER_PASSWORD" != "$WEFT_LOCAL_BUILD_WRITER_PASSWORD" ]] || {
  echo 'Build profile passwords must be independent.' >&2; exit 1;
}
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname weft <<'SQL'
\getenv build_reader_password WEFT_LOCAL_BUILD_READER_PASSWORD
\getenv build_writer_password WEFT_LOCAL_BUILD_WRITER_PASSWORD
BEGIN;
CREATE ROLE weft_build_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'build_reader_password';
CREATE ROLE weft_build_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'build_writer_password';
GRANT CONNECT ON DATABASE weft TO weft_build_reader,weft_build_writer;
COMMIT;
SQL
