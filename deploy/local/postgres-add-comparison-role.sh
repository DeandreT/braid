#!/usr/bin/env bash
set -euo pipefail
# Explicit administrator action for an existing local database before migration.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
: "${BRAID_LOCAL_COMPARISON_PASSWORD:?Set the separate trusted comparison password}"
[[ "$POSTGRES_DB" == braid ]] || { echo 'This script targets only the local braid database.' >&2; exit 1; }
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv comparison_password BRAID_LOCAL_COMPARISON_PASSWORD
BEGIN;
CREATE ROLE braid_comparison_settler LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'comparison_password';
GRANT CONNECT ON DATABASE braid TO braid_comparison_settler;
COMMIT;
SQL
