#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before upgrading an
# existing ownership-only database. Existing roles/passwords are never changed.
: "${WEFT_LOCAL_INGEST_PASSWORD:?Set a separate ingestion password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv ingest_password WEFT_LOCAL_INGEST_PASSWORD
BEGIN;
CREATE ROLE weft_ingest LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'ingest_password';
GRANT CONNECT ON DATABASE weft TO weft_ingest;
COMMIT;
SQL
