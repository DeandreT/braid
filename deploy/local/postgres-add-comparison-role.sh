#!/usr/bin/env bash
set -euo pipefail
# Explicit administrator action for an existing local database before migration.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
: "${WEFT_LOCAL_COMPARISON_PASSWORD:?Set the separate trusted comparison password}"
[[ "$POSTGRES_DB" == weft ]] || { echo 'This script targets only the local weft database.' >&2; exit 1; }
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv comparison_password WEFT_LOCAL_COMPARISON_PASSWORD
BEGIN;
CREATE ROLE weft_comparison_settler LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'comparison_password';
GRANT CONNECT ON DATABASE weft TO weft_comparison_settler;
COMMIT;
SQL
