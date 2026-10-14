#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. Existing logins, passwords and grants stay intact.
: "${WEFT_LOCAL_CREATOR_PASSWORD:?Set a separate project creator password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv creator_password WEFT_LOCAL_CREATOR_PASSWORD
BEGIN;
CREATE ROLE weft_project_creator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'creator_password';
GRANT CONNECT ON DATABASE weft TO weft_project_creator;
COMMIT;
SQL
