#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before upgrading an
# existing database. Existing logins, passwords and grants are not changed.
: "${BRAID_LOCAL_CATALOG_PASSWORD:?Set a separate catalog reader password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv catalog_password BRAID_LOCAL_CATALOG_PASSWORD
BEGIN;
CREATE ROLE braid_catalog_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'catalog_password';
GRANT CONNECT ON DATABASE braid TO braid_catalog_reader;
COMMIT;
SQL
