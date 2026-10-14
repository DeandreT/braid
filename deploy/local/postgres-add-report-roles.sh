#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. Both new logins are created in one transaction.
: "${WEFT_LOCAL_REPORT_PASSWORD:?Set a separate report reader password}"
: "${WEFT_LOCAL_DERIVATION_PASSWORD:?Set a separate report derivation password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv report_password WEFT_LOCAL_REPORT_PASSWORD
\getenv derivation_password WEFT_LOCAL_DERIVATION_PASSWORD
BEGIN;
CREATE ROLE weft_report_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'report_password';
CREATE ROLE weft_deriver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'derivation_password';
GRANT CONNECT ON DATABASE weft TO weft_report_reader, weft_deriver;
COMMIT;
SQL
