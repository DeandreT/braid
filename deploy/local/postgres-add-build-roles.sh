#!/usr/bin/env bash
# Explicit administrator upgrade before migration 0018. Does not rotate or adopt existing roles.
set -euo pipefail
: "${BRAID_LOCAL_BUILD_READER_PASSWORD:?Set a separate build reader password}"
: "${BRAID_LOCAL_BUILD_WRITER_PASSWORD:?Set a separate build writer password}"
[[ "$BRAID_LOCAL_BUILD_READER_PASSWORD" != "$BRAID_LOCAL_BUILD_WRITER_PASSWORD" ]] || {
  echo 'Build profile passwords must be independent.' >&2; exit 1;
}
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname braid <<'SQL'
\getenv build_reader_password BRAID_LOCAL_BUILD_READER_PASSWORD
\getenv build_writer_password BRAID_LOCAL_BUILD_WRITER_PASSWORD
BEGIN;
CREATE ROLE braid_build_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'build_reader_password';
CREATE ROLE braid_build_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'build_writer_password';
GRANT CONNECT ON DATABASE braid TO braid_build_reader,braid_build_writer;
COMMIT;
SQL
