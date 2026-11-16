#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. Either existing role refuses the whole transaction.
: "${BRAID_LOCAL_SESSION_READER_PASSWORD:?Set a separate session reader password}"
: "${BRAID_LOCAL_SESSION_WRITER_PASSWORD:?Set a separate session writer password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv session_reader_password BRAID_LOCAL_SESSION_READER_PASSWORD
\getenv session_writer_password BRAID_LOCAL_SESSION_WRITER_PASSWORD
BEGIN;
CREATE ROLE braid_session_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_reader_password';
CREATE ROLE braid_session_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_writer_password';
GRANT CONNECT ON DATABASE braid TO braid_session_reader, braid_session_writer;
COMMIT;
SQL
