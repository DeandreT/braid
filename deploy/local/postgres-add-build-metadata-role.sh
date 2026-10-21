#!/usr/bin/env bash
set -euo pipefail
: "${WEFT_LOCAL_BUILD_METADATA_PASSWORD:?Set an independent build metadata reader password}"
# Explicit administrator upgrade step; never called by an application.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv password WEFT_LOCAL_BUILD_METADATA_PASSWORD
BEGIN;
CREATE ROLE weft_build_metadata_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'password';
GRANT CONNECT ON DATABASE weft TO weft_build_metadata_reader;
COMMIT;
SQL
