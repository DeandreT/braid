#!/usr/bin/env bash
set -euo pipefail

# Explicit administrator step for an existing local volume, before stop/migrate/start.
# All CREATE ROLE statements and CONNECT grants are atomic; existing roles refuse.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
[[ "$POSTGRES_DB" == braid ]] || { echo 'This script targets only the local braid database.' >&2; exit 1; }
: "${BRAID_LOCAL_CHAT_READER_PASSWORD:?Set a separate chat reader password}"
: "${BRAID_LOCAL_CHAT_WRITER_PASSWORD:?Set a separate chat writer password}"
: "${BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD:?Set a separate chat lifecycle password}"
: "${BRAID_LOCAL_CHAT_APPROVER_PASSWORD:?Set a separate chat approver password}"
[[ "$BRAID_LOCAL_CHAT_READER_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$BRAID_LOCAL_CHAT_READER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_READER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" ]] || {
  echo 'All chat profile passwords must be independent.' >&2; exit 1;
}
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv chat_reader_password BRAID_LOCAL_CHAT_READER_PASSWORD
\getenv chat_writer_password BRAID_LOCAL_CHAT_WRITER_PASSWORD
\getenv chat_lifecycle_password BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD
\getenv chat_approver_password BRAID_LOCAL_CHAT_APPROVER_PASSWORD
BEGIN;
CREATE ROLE braid_chat_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_reader_password';
CREATE ROLE braid_chat_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_writer_password';
CREATE ROLE braid_chat_lifecycle LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_lifecycle_password';
CREATE ROLE braid_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_approver_password';
GRANT CONNECT ON DATABASE braid TO braid_chat_reader,braid_chat_writer,braid_chat_lifecycle,braid_chat_approver;
GRANT SET ON PARAMETER braid.organization_id, braid.chat_principal_id TO braid_migrator;
COMMIT;
SQL
