#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_API_AUDIT_WRITER_PASSWORD:?Set an independent API audit writer password}"
# Explicit administrator upgrade step. No password rotation or existing-role adoption.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv api_audit_writer_password BRAID_LOCAL_API_AUDIT_WRITER_PASSWORD
BEGIN;
CREATE ROLE braid_api_audit_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'api_audit_writer_password';
GRANT CONNECT ON DATABASE braid TO braid_api_audit_writer;
COMMIT;
SQL
