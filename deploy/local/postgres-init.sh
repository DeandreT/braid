#!/usr/bin/env bash
set -euo pipefail

# This runs once inside this compose project's new, dedicated database.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv migrator_password WEFT_LOCAL_MIGRATOR_PASSWORD
\getenv runtime_password WEFT_LOCAL_RUNTIME_PASSWORD
CREATE ROLE weft_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'migrator_password';
CREATE ROLE weft_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'runtime_password';
ALTER DATABASE weft OWNER TO weft_migrator;
REVOKE ALL ON DATABASE weft FROM PUBLIC;
GRANT CONNECT ON DATABASE weft TO weft_runtime;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
