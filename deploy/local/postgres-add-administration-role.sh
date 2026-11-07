#!/usr/bin/env bash
set -euo pipefail
: "${WEFT_LOCAL_ADMINISTRATION_PASSWORD:?Set a separate administration password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv administration_password WEFT_LOCAL_ADMINISTRATION_PASSWORD
CREATE ROLE weft_administration LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'administration_password';
GRANT CONNECT ON DATABASE weft TO weft_administration;
SQL
